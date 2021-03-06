using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AltitudeAngel.IsolatedPlugin.Common;
using AltitudeAngel.IsolatedPlugin.Common.Maps;
using AltitudeAngelWings.ApiClient.Client;
using AltitudeAngelWings.ApiClient.Models;
using AltitudeAngelWings.Properties;
using AltitudeAngelWings.Service.FlightData;
using AltitudeAngelWings.Service.Messaging;
using DotNetOpenAuth.OAuth2;
using GeoJSON.Net;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using GMap.NET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AltitudeAngelWings.Service
{
    public class AltitudeAngelService : IDisposable
    {
        public ObservableProperty<bool> IsSignedIn { get; }
        public ObservableProperty<WeatherInfo> WeatherReport { get; }
        public ObservableProperty<Unit> SentTelemetry { get; }
        public UserProfileInfo CurrentUser { get; private set; }

        private bool _grounddata = true;
        public bool GroundDataDisplay
        {
            get
            {
                if (String.IsNullOrEmpty(_missionPlanner.LoadSetting("AA.Ground")))
                    return _grounddata;
                _grounddata = bool.Parse(_missionPlanner.LoadSetting("AA.Ground"));
                return _grounddata;
            }
            set
            {
                _grounddata = value;
                _missionPlanner.SaveSetting("AA.Ground", _grounddata.ToString());
            }
        }

        private bool _airdata = false;
        public bool AirDataDisplay
        {
            get
            {
                if (String.IsNullOrEmpty(_missionPlanner.LoadSetting("AA.Air")))
                    return _airdata;
                _airdata = bool.Parse(_missionPlanner.LoadSetting("AA.Air"));
                return _airdata;
            }
            set
            {
                _grounddata = value;
                _missionPlanner.SaveSetting("AA.Air", _grounddata.ToString());
            }
        }

        public AltitudeAngelService(
            IMessagesService messagesService,
            IMissionPlanner missionPlanner,
            FlightDataService flightDataService
            )
        {
            _messagesService = messagesService;
            _missionPlanner = missionPlanner;
            _flightDataService = flightDataService;
            IsSignedIn = new ObservableProperty<bool>(false);
            WeatherReport = new ObservableProperty<WeatherInfo>();
            SentTelemetry = new ObservableProperty<Unit>();

            CreateClient((url, apiUrl, state) =>
                new AltitudeAngelClient(url, apiUrl, state,
                    (authUrl, existingState) => new AltitudeAngelHttpHandlerFactory(authUrl, existingState)));

            _disposer.Add(_missionPlanner.FlightDataMap
                .MapChanged
                .Throttle(TimeSpan.FromSeconds(1))
                .Subscribe(i => UpdateMapData(_missionPlanner.FlightDataMap)));

            TryConnect();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task SignInAsync()
        {
            try
            {
                // Load the user's profile, will trigger auth
                await LoadUserProfile();

                // Save the token from the auth process
                _missionPlanner.SaveSetting("AAWings.Token", JsonConvert.SerializeObject(_aaClient.AuthorizationState));

                SignedIn(true);
            }
            catch (Exception ex)
            {
                await _messagesService.AddMessageAsync($"There was a problem signing you in.\r\n{ex.Message}");
            }
        }

        public Task DisconnectAsync()
        {
            _missionPlanner.ClearSetting("AAWings.Token");
            _aaClient.Disconnect();
            SignedOut();

            return null;
        }

        /// <summary>
        ///     Updates a map with the latest ground / air data
        /// </summary>
        /// <param name="map">The map to update</param>
        /// <returns></returns>
        public async Task UpdateMapData(IMap map)
        {
            if (!IsSignedIn)
                return;

            RectLatLng area = map.GetViewArea();
            await _messagesService.AddMessageAsync($"Map area {area.Top}, {area.Bottom}, {area.Left}, {area.Right}");

            AAFeatureCollection mapData = await _aaClient.GetMapData(area);

            await _messagesService.AddMessageAsync($"Map area Loaded {area.Top}, {area.Bottom}, {area.Left}, {area.Right}");

            IOverlay airOverlay = map.GetOverlay("AAMapData.Air", true);
            IOverlay groundOverlay = map.GetOverlay("AAMapData.Ground", true);

            bool groundDataExcluded = mapData.ExcludedData.Any(i => i.SelectToken("detail.name")?.Value<string>() == "Ground Hazards");
            groundOverlay.IsVisible = GroundDataDisplay;
            airOverlay.IsVisible = AirDataDisplay;

            // Only get the features that have no lower alt or start below 152m. Ignoring datum for now...
            IEnumerable<Feature> features = mapData.Features.ToList();


            foreach (Feature feature in features)
            {
                IOverlay overlay = string.Equals((string)feature.Properties.Get("category"), "airspace")
                    ? airOverlay
                    : groundOverlay;

                var altitude = ((JObject)feature.Properties.Get("altitudeFloor"))?.ToObject<Altitude>();

                if (altitude == null || altitude.Meters <= 152)
                {
                    if (!GroundDataDisplay)
                    {
                        if (overlay.PolygonExists(feature.Id))

                            continue;
                    }
                }
                else
                {
                    if (!AirDataDisplay)
                    {
                        continue;
                    }
                }

                switch (feature.Geometry.Type)
                {
                    case GeoJSONObjectType.Point:
                        break;
                    case GeoJSONObjectType.MultiPoint:
                        break;
                    case GeoJSONObjectType.LineString:
                        {
                            if (!overlay.LineExists(feature.Id))
                            {
                                var line = (LineString)feature.Geometry;
                                List<PointLatLng> coordinates = line.Coordinates.OfType<GeographicPosition>()
                                                                    .Select(c => new PointLatLng(c.Latitude, c.Longitude))
                                                                    .ToList();
                                overlay.AddLine(feature.Id, coordinates, new ColorInfo { StrokeColor = 0xFFFF0000 }, feature);
                            }
                        }
                        break;

                    case GeoJSONObjectType.MultiLineString:
                        break;
                    case GeoJSONObjectType.Polygon:
                        {
                            if (!overlay.PolygonExists(feature.Id))
                            {
                                var poly = (Polygon)feature.Geometry;
                                List<PointLatLng> coordinates =
                                    poly.Coordinates[0].Coordinates.OfType<GeographicPosition>()
                                                       .Select(c => new PointLatLng(c.Latitude, c.Longitude))
                                                       .ToList();

                                ColorInfo colorInfo = feature.ToColorInfo();
                                colorInfo.StrokeColor = 0xFFFF0000;
                                overlay.AddPolygon(feature.Id, coordinates, colorInfo, feature);
                            }
                        }
                        break;
                    case GeoJSONObjectType.MultiPolygon:
                        break;
                    case GeoJSONObjectType.GeometryCollection:
                        break;
                    case GeoJSONObjectType.Feature:
                        break;
                    case GeoJSONObjectType.FeatureCollection:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }


        /// <summary>
        ///     Update the AA UI with the latest weather
        /// </summary>
        /// <param name="center">The point to get weather for</param>
        /// <returns></returns>
        public async Task UpdateWeatherData(PointLatLng center)
        {
            WeatherReport.Value = await _aaClient.GetWeather(center);
        }

        private void CreateClient(AltitudeAngelClient.Create aaClientFactory)
        {
            string stateString = _missionPlanner.LoadSetting("AAWings.Token");
            AuthorizationState existingState = null;
            if (stateString != null)
            {
                existingState = JsonConvert.DeserializeObject<AuthorizationState>(stateString);
            }

            _aaClient = aaClientFactory(ConfigurationManager.AppSettings["AuthURL"],
                ConfigurationManager.AppSettings["APIURL"], existingState);
        }

        private async void TryConnect()
        {
            if (_missionPlanner.LoadSetting("AAWings.Token") != null)
            {
                await SignInAsync();
            }
        }

        private async Task LoadUserProfile()
        {
            CurrentUser = await _aaClient.GetUserProfile();
        }

        private void SignedIn(bool isSignedIn)
        {
            IsSignedIn.Value = isSignedIn;
            _messagesService.AddMessageAsync("Connected to Altitude Angel.");

            if (isSignedIn)
            {
                _messagesService.AddMessageAsync("Loading map data...")
                    .ContinueWith(async i =>
                    {
                        try
                        {
                            await UpdateMapData(_missionPlanner.FlightDataMap);
                            await _messagesService.AddMessageAsync("Map data loaded");
                        }
                        catch
                        {
                        }
                    });

                // Should really move this to a manual trigger or on arm as the map might not be in the correct position
                // And we only want to do it occasionally
                _messagesService.AddMessageAsync("Loading weather info...")
                    .ContinueWith(async i =>
                    {
                        try
                        {
                            await UpdateWeatherData(_missionPlanner.FlightDataMap.GetCenter());
                            await _messagesService.AddMessageAsync("Weather loaded");
                        }
                        catch
                        {
                        }
                    });
            }
        }

        private void SignedOut()
        {
            IsSignedIn.Value = false;
        }

        private void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                _disposer?.Dispose();
                _disposer = null;
            }
        }

        private readonly FlightDataService _flightDataService;
        private readonly IMessagesService _messagesService;
        private readonly IMissionPlanner _missionPlanner;
        private AltitudeAngelClient _aaClient;
        private CompositeDisposable _disposer = new CompositeDisposable();

        public AltitudeAngelClient Client
        {
            get { return _aaClient; }
        }
    }
}
