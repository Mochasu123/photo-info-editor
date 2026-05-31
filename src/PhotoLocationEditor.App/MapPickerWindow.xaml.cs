using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;

namespace PhotoLocationEditor.App;

public partial class MapPickerWindow : Window
{
    private readonly GpsCoordinate? _initialCoordinate;
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService = new();
    private MapProvider _provider = MapProvider.AMap;
    private MapProvider? _loadedProvider;
    private MapLayer _mapLayer = MapLayer.AMapStandard;
    private bool _isLoaded;
    private bool _isUpdatingLayerOptions;
    private bool _isSelectingSearchResult;

    public MapPickerWindow(
        string title,
        string useButtonText,
        string cancelButtonText,
        GpsCoordinate? initialCoordinate,
        AppSettings settings,
        string? notice = null)
    {
        _initialCoordinate = initialCoordinate;
        _settings = settings;
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        SearchTextBox.Text = string.Empty;
        AmapKeyTextBox.Text = _settings.AMapJsKey;
        AmapSecurityTextBox.Text = _settings.AMapSecurityJsCode;
        SearchResultsListBox.ItemsSource = SearchResults;
        MapProviderComboBox.SelectedIndex = HasAmapSettings ? 0 : 1;
        ConfigureLayerOptions();
        UseButton.Content = useButtonText;
        CancelButton.Content = cancelButtonText;
        UseButton.ToolTip = notice;
        MapWebView.ToolTip = notice;
        UseButton.IsEnabled = initialCoordinate is not null;
        SelectedCoordinate = initialCoordinate;
        UpdateCoordinateText();
        Loaded += MapPickerWindow_Loaded;
    }

    public GpsCoordinate? SelectedCoordinate { get; private set; }
    public ObservableCollection<MapSearchResult> SearchResults { get; } = new();
    private bool HasAmapSettings =>
        !string.IsNullOrWhiteSpace(_settings.AMapJsKey) &&
        !string.IsNullOrWhiteSpace(_settings.AMapSecurityJsCode);

    private async void MapPickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = HasAmapSettings ? "Loading 高德地图..." : "Loading OpenStreetMap...";
        try
        {
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _isLoaded = true;
            LoadCurrentProvider();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, "Map failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "picked")
            {
                if (type.GetString() == "searchResults")
                {
                    ApplySearchResults(root);
                }

                return;
            }

            var latitude = root.GetProperty("lat").GetDouble();
            var longitude = root.GetProperty("lon").GetDouble();
            var pickedCoordinate = new GpsCoordinate(latitude, longitude);
            SelectedCoordinate = _provider == MapProvider.AMap
                ? CoordinateTransform.Gcj02ToWgs84(pickedCoordinate)
                : pickedCoordinate;
            UseButton.IsEnabled = true;
            UpdateCoordinateText();
            if (root.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                StatusText.Text = _provider == MapProvider.AMap
                    ? $"Point picked: {name.GetString()} (高德地图 GCJ-02 -> EXIF WGS-84)"
                    : $"Point picked: {name.GetString()}";
            }
            else
            {
                StatusText.Text = _provider == MapProvider.AMap
                    ? "Point picked. 高德地图 GCJ-02 was converted to EXIF WGS-84."
                    : "Point picked.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ApplySearchResults(JsonElement root)
    {
        _isSelectingSearchResult = true;
        SearchResults.Clear();
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                SearchResults.Add(new MapSearchResult(
                    item.GetProperty("name").GetString() ?? "(unnamed)",
                    item.GetProperty("address").GetString() ?? string.Empty,
                    item.GetProperty("lat").GetDouble(),
                    item.GetProperty("lon").GetDouble()));
            }
        }

        SearchResultsListBox.SelectedIndex = SearchResults.Count == 1 ? 0 : -1;
        _isSelectingSearchResult = false;
        StatusText.Text = SearchResults.Count == 0
            ? "No POI or address found."
            : $"Found {SearchResults.Count} result(s).";
    }

    private void UpdateCoordinateText()
    {
        CoordinateText.Text = SelectedCoordinate is null
            ? "No coordinate selected"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.######}, {1:0.######}",
                SelectedCoordinate.Latitude,
                SelectedCoordinate.Longitude);
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCoordinate is null)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchAsync();
            e.Handled = true;
        }
    }

    private async Task SearchAsync()
    {
        var keyword = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || MapWebView.CoreWebView2 is null)
        {
            return;
        }

        if (_provider == MapProvider.OpenStreetMap)
        {
            StatusText.Text = "Search is available in 高德地图 mode. Switch to 高德地图 for POI search.";
            return;
        }

        StatusText.Text = $"Searching: {keyword}";
        SearchResults.Clear();
        await MapWebView.CoreWebView2.ExecuteScriptAsync($"window.searchPoi({JsonSerializer.Serialize(keyword)});");
    }

    private void SaveAmapSettings_Click(object sender, RoutedEventArgs e)
    {
        var hadSettings = HasAmapSettings;
        _settings.AMapJsKey = AmapKeyTextBox.Text.Trim();
        _settings.AMapSecurityJsCode = AmapSecurityTextBox.Text.Trim();
        _settingsService.Save(_settings);
        StatusText.Text = HasAmapSettings
            ? "高德配置已保存，可以切换到高德地图。"
            : "高德配置为空，将继续使用 OpenStreetMap。";

        if (!hadSettings && HasAmapSettings)
        {
            MapProviderComboBox.SelectedIndex = 0;
            LoadCurrentProvider();
        }
    }

    private async void SearchResultsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isSelectingSearchResult ||
            SearchResultsListBox.SelectedItem is not MapSearchResult result ||
            MapWebView.CoreWebView2 is null)
        {
            return;
        }

        await MapWebView.CoreWebView2.ExecuteScriptAsync(
            string.Format(
                CultureInfo.InvariantCulture,
                "window.pickFromHost({0:R},{1:R},{2});",
                result.Longitude,
                result.Latitude,
                JsonSerializer.Serialize(result.Name)));
    }

    private void MapProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MapProviderComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
        {
            return;
        }

        _provider = item.Tag?.ToString() == "osm" ? MapProvider.OpenStreetMap : MapProvider.AMap;
        ConfigureLayerOptions();
        if (_isLoaded)
        {
            LoadCurrentProvider();
        }
    }

    private async void MapLayerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingLayerOptions || MapLayerComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
        {
            return;
        }

        _mapLayer = ParseMapLayer(item.Tag?.ToString());
        if (!_isLoaded || MapWebView.CoreWebView2 is null)
        {
            return;
        }

        if (_provider == MapProvider.AMap)
        {
            await MapWebView.CoreWebView2.ExecuteScriptAsync($"window.setAmapLayer && window.setAmapLayer('{GetAmapLayerName()}');");
            StatusText.Text = "高德地图 layer updated.";
        }
        else
        {
            await MapWebView.CoreWebView2.ExecuteScriptAsync($"window.setOsmLayer && window.setOsmLayer('{GetOsmLayerName()}');");
            StatusText.Text = "OpenStreetMap layer updated.";
        }
    }

    private void ConfigureLayerOptions()
    {
        if (MapLayerComboBox is null)
        {
            return;
        }

        _isUpdatingLayerOptions = true;
        MapLayerComboBox.Items.Clear();
        if (_provider == MapProvider.AMap)
        {
            AddLayerOption(MapLayer.AMapStandard, "标准图");
            AddLayerOption(MapLayer.AMapSatellite, "卫星图");
            AddLayerOption(MapLayer.AMapSatelliteRoad, "卫星+路网");
            AddLayerOption(MapLayer.AMapTraffic, "实时路况");
            _mapLayer = MapLayer.AMapStandard;
        }
        else
        {
            AddLayerOption(MapLayer.OsmStandard, "Standard");
            AddLayerOption(MapLayer.OsmHumanitarian, "Humanitarian");
            AddLayerOption(MapLayer.OsmTopo, "OpenTopoMap");
            AddLayerOption(MapLayer.EsriSatellite, "Esri Satellite");
            _mapLayer = MapLayer.OsmStandard;
        }

        MapLayerComboBox.SelectedIndex = 0;
        _isUpdatingLayerOptions = false;
    }

    private void AddLayerOption(MapLayer layer, string label)
    {
        MapLayerComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Tag = layer.ToString(),
            Content = label
        });
    }

    private static MapLayer ParseMapLayer(string? value)
    {
        return Enum.TryParse<MapLayer>(value, out var layer) ? layer : MapLayer.AMapStandard;
    }

    private void LoadCurrentProvider()
    {
        if (MapWebView.CoreWebView2 is null)
        {
            return;
        }

        SearchButton.IsEnabled = _provider == MapProvider.AMap && HasAmapSettings;
        SearchTextBox.IsEnabled = _provider == MapProvider.AMap && HasAmapSettings;
        MapLayerComboBox.IsEnabled = true;
        if (_loadedProvider == _provider)
        {
            StatusText.Text = _provider == MapProvider.AMap
                ? "高德地图 ready. POI search is available."
                : "OpenStreetMap ready. Click map to pick GPS.";
            return;
        }

        if (_provider == MapProvider.AMap && !HasAmapSettings)
        {
            StatusText.Text = "请先在设置里填写高德 JS Key 和 Security JS Code。";
            return;
        }

        StatusText.Text = _provider == MapProvider.AMap ? "Loading 高德地图..." : "Loading OpenStreetMap...";
        MapWebView.NavigateToString(_provider == MapProvider.AMap ? BuildAMapHtml() : BuildOsmHtml());
        _loadedProvider = _provider;
        StatusText.Text = _provider == MapProvider.AMap
            ? "高德地图 ready. POI search is available."
            : "OpenStreetMap ready. Click map to pick GPS.";
    }

    private (double Latitude, double Longitude, int Zoom) GetInitialView()
    {
        return (
            _initialCoordinate?.Latitude ?? 39.9042,
            _initialCoordinate?.Longitude ?? 116.4074,
            _initialCoordinate is null ? 4 : 15);
    }

    private string BuildMapHtml()
    {
        return BuildAMapHtml();
    }

    private string GetAmapLayerName()
    {
        return _mapLayer switch
        {
            MapLayer.AMapSatellite => "satellite",
            MapLayer.AMapSatelliteRoad => "satellite-road",
            MapLayer.AMapTraffic => "traffic",
            _ => "standard"
        };
    }

    private string GetOsmLayerName()
    {
        return _mapLayer switch
        {
            MapLayer.OsmHumanitarian => "humanitarian",
            MapLayer.OsmTopo => "topo",
            MapLayer.EsriSatellite => "satellite",
            _ => "standard"
        };
    }

    private string BuildAMapHtml()
    {
        var (latitude, longitude, zoom) = GetInitialView();
        var amapCoordinate = CoordinateTransform.Wgs84ToGcj02(new GpsCoordinate(latitude, longitude));
        latitude = amapCoordinate.Latitude;
        longitude = amapCoordinate.Longitude;

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <style>
    html, body, #map { height: 100%; margin: 0; }
    .pin {
      width: 22px;
      height: 22px;
      background: #ef4444;
      border: 2px solid #ffffff;
      border-radius: 50% 50% 50% 0;
      box-shadow: 0 2px 8px rgba(0,0,0,.28);
      transform: rotate(-45deg);
    }
    .pin::after {
      content: '';
      position: absolute;
      width: 8px;
      height: 8px;
      left: 5px;
      top: 5px;
      background: #ffffff;
      border-radius: 50%;
    }
  </style>
</head>
<body>
  <div id="map"></div>
  <script>
    window._AMapSecurityConfig = {
      securityJsCode: {{JsonEncodedText(_settings.AMapSecurityJsCode)}}
    };
  </script>
  <script src="https://webapi.amap.com/maps?v=2.0&key={{Uri.EscapeDataString(_settings.AMapJsKey)}}&plugin=AMap.PlaceSearch,AMap.Geocoder,AMap.ToolBar,AMap.Scale"></script>
  <script>
    const map = new AMap.Map('map', {
      viewMode: '2D',
      zoom: {{zoom}},
      zooms: [3, 20],
      center: [{{longitude.ToString("R", CultureInfo.InvariantCulture)}}, {{latitude.ToString("R", CultureInfo.InvariantCulture)}}]
    });
    const standardLayers = [new AMap.TileLayer()];
    const satelliteLayers = [new AMap.TileLayer.Satellite()];
    const satelliteRoadLayers = [new AMap.TileLayer.Satellite(), new AMap.TileLayer.RoadNet()];
    const trafficLayers = [new AMap.TileLayer(), new AMap.TileLayer.Traffic({ zIndex: 10 })];
    window.setAmapLayer = function(type) {
      if (type === 'satellite') {
        map.setLayers(satelliteLayers);
      } else if (type === 'satellite-road') {
        map.setLayers(satelliteRoadLayers);
      } else if (type === 'traffic') {
        map.setLayers(trafficLayers);
      } else {
        map.setLayers(standardLayers);
      }
    };
    window.setAmapLayer('{{GetAmapLayerName()}}');
    map.addControl(new AMap.ToolBar());
    map.addControl(new AMap.Scale());
    let marker = null;
    let placeSearch = new AMap.PlaceSearch({ city: '全国', pageSize: 10, pageIndex: 1 });
    let geocoder = new AMap.Geocoder({ city: '全国' });
    function postPick(lnglat, name) {
      window.chrome.webview.postMessage({ type: 'picked', lat: lnglat.getLat(), lon: lnglat.getLng(), name: name || '' });
    }
    function pick(lnglat, name) {
      if (!marker) {
        marker = new AMap.Marker({
          position: lnglat,
          draggable: true,
          content: '<div class="pin"></div>',
          offset: new AMap.Pixel(-11, -28)
        });
        map.add(marker);
        marker.on('dragend', event => pick(event.lnglat));
      } else {
        marker.setPosition(lnglat);
      }
      map.setCenter(lnglat);
      postPick(lnglat, name);
    }
    window.pickFromHost = function(lon, lat, name) {
      const lnglat = new AMap.LngLat(lon, lat);
      map.setZoomAndCenter(19, lnglat);
      pick(lnglat, name || '');
    };
    function postSearchResults(items) {
      window.chrome.webview.postMessage({ type: 'searchResults', items: items || [] });
    }
    map.on('click', event => pick(event.lnglat));
    function geocodeAddress(keyword) {
      geocoder.getLocation(keyword, function(status, result) {
        if (status !== 'complete' || !result.geocodes || !result.geocodes.length) {
          postSearchResults([]);
          return;
        }
        postSearchResults(result.geocodes.slice(0, 10).map(function(geocode) {
          return {
            name: geocode.formattedAddress || keyword,
            address: 'Address matched by AMap Geocoder',
            lat: geocode.location.getLat(),
            lon: geocode.location.getLng()
          };
        }));
      });
    }
    window.searchPoi = function(keyword) {
      placeSearch.search(keyword, function(status, result) {
        if (status !== 'complete' || !result.poiList || !result.poiList.pois.length) {
          geocodeAddress(keyword);
          return;
        }
        postSearchResults(result.poiList.pois.slice(0, 10).map(function(poi) {
          return {
            name: poi.name || '(unnamed)',
            address: poi.address || poi.type || '',
            lat: poi.location.getLat(),
            lon: poi.location.getLng()
          };
        }));
      });
    };
    {{(_initialCoordinate is null ? "" : "pick(new AMap.LngLat(" + longitude.ToString("R", CultureInfo.InvariantCulture) + "," + latitude.ToString("R", CultureInfo.InvariantCulture) + "));")}}
  </script>
</body>
</html>
""";
    }

    private string BuildOsmHtml()
    {
        var (latitude, longitude, zoom) = GetInitialView();

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
  <style>
    html, body, #map { height: 100%; margin: 0; }
    .hint {
      position: absolute;
      z-index: 999;
      left: 12px;
      top: 12px;
      background: rgba(255,255,255,.92);
      padding: 8px 10px;
      border-radius: 6px;
      font: 13px system-ui, -apple-system, Segoe UI, sans-serif;
      box-shadow: 0 1px 4px rgba(0,0,0,.2);
    }
  </style>
</head>
<body>
  <div id="map"></div>
  <div class="hint">OpenStreetMap: click map to pick GPS</div>
  <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
  <script>
    const map = L.map('map').setView([{{latitude.ToString("R", CultureInfo.InvariantCulture)}}, {{longitude.ToString("R", CultureInfo.InvariantCulture)}}], {{zoom}});
    const layers = {
      standard: L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors'
      }),
      humanitarian: L.tileLayer('https://{s}.tile.openstreetmap.fr/hot/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors, Tiles style by HOT'
      }),
      topo: L.tileLayer('https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png', {
        maxZoom: 17,
        attribution: 'Map data: &copy; OpenStreetMap contributors, SRTM | Map style: &copy; OpenTopoMap'
      }),
      satellite: L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
        maxZoom: 19,
        attribution: 'Tiles &copy; Esri'
      })
    };
    let activeLayer = null;
    window.setOsmLayer = function(type) {
      const next = layers[type] || layers.standard;
      if (activeLayer) {
        map.removeLayer(activeLayer);
      }
      activeLayer = next;
      activeLayer.addTo(map);
    };
    window.setOsmLayer('{{GetOsmLayerName()}}');
    L.control.layers({
      'Standard': layers.standard,
      'Humanitarian': layers.humanitarian,
      'OpenTopoMap': layers.topo,
      'Esri Satellite': layers.satellite
    }, null, { collapsed: false }).addTo(map);
    let marker = null;
    function pick(latlng) {
      if (!marker) {
        marker = L.marker(latlng, { draggable: true }).addTo(map);
        marker.on('dragend', () => pick(marker.getLatLng()));
      } else {
        marker.setLatLng(latlng);
      }
      window.chrome.webview.postMessage({ type: 'picked', lat: latlng.lat, lon: latlng.lng, name: '' });
    }
    map.on('click', event => pick(event.latlng));
    {{(_initialCoordinate is null ? "" : "pick(L.latLng(" + latitude.ToString("R", CultureInfo.InvariantCulture) + "," + longitude.ToString("R", CultureInfo.InvariantCulture) + "));")}}
  </script>
</body>
</html>
""";
    }

    private enum MapProvider
    {
        AMap,
        OpenStreetMap
    }

    private enum MapLayer
    {
        AMapStandard,
        AMapSatellite,
        AMapSatelliteRoad,
        AMapTraffic,
        OsmStandard,
        OsmHumanitarian,
        OsmTopo,
        EsriSatellite
    }

    public sealed record MapSearchResult(string Name, string Address, double Latitude, double Longitude);

    private static string JsonEncodedText(string value)
    {
        return JsonSerializer.Serialize(value);
    }
}
