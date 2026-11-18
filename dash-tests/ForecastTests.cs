using System.Collections.Generic;
using Xunit;
using static TizenNotifier.Fixtures;

namespace TizenNotifier
{
    public class ForecastTests
    {
        private static Dictionary<string, object> Change(string id, string state) => P("c", P(id, P("+", P("s", state))));

        [Fact]
        public void ResubscribesWhenTheWeatherEntityReturns()
        {
            var cfg = P("views", new List<object> { P("title", "W", "type", "sections", "sections", new List<object> { P("type", "grid", "cards", new List<object> {
                P("type", "weather-forecast", "entity", "weather.home", "forecast_type", "daily") }) }) });
            var (s, _) = Session("fixture-sections", P("scale", 1, "theme", P("name", "ambient")), cfg);
            s.OnEntities(P("a", P("weather.home", P("s", "sunny", "a", P("temperature", 70.0)))));
            Render(s);                                       // the card asks for the forecast
            s.OnEntities(Change("weather.home", "cloudy"));
            Assert.Equal(0, s.ForecastResubscribes);         // an ordinary change keeps the subscription
            s.OnEntities(Change("weather.home", "unavailable"));
            Assert.Equal(0, s.ForecastResubscribes);
            s.OnEntities(Change("weather.home", "sunny"));   // back (the integration reloaded): subscribe again
            Assert.Equal(1, s.ForecastResubscribes);
            s.OnEntities(Change("sensor.other", "sunny"));
            Assert.Equal(1, s.ForecastResubscribes);
        }
    }
}
