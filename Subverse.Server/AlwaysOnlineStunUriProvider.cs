namespace Subverse.Server
{
    internal class AlwaysOnlineStunUriProvider : IStunUriProvider, IDisposable
    {
        private const string DEFAULT_CONFIG_ALWAYS_ONLINE_URL = "https://raw.githubusercontent.com/pradt2/always-online-stun/master/valid_ipv4s.txt";

        private readonly IConfiguration _configuration;
        private readonly string _configAlwaysOnlineUrl;

        private readonly HttpClient _http;

        public AlwaysOnlineStunUriProvider(IConfiguration configuration)
        {
            _configuration = configuration;

            _configAlwaysOnlineUrl = _configuration.GetConnectionString("AlwaysOnlineStun") ??
                DEFAULT_CONFIG_ALWAYS_ONLINE_URL;

            _http = new HttpClient();
        }

        public async IAsyncEnumerable<string> GetAvailableAsync()
        {
            using (var responseStream = await _http.GetStreamAsync(_configAlwaysOnlineUrl))
            using (var responseReader = new StreamReader(responseStream))
            {
                string? line;
                while ((line = await responseReader.ReadLineAsync()) is not null)
                {
                    yield return $"stun://{line}";
                }
            }
        }

        public void Dispose()
        {
            ((IDisposable)_http).Dispose();
        }
    }
}
