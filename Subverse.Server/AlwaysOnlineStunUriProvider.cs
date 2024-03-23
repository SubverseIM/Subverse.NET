using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace Subverse.Server
{
    internal class AlwaysOnlineStunUriProvider : IStunUriProvider
    {
        private const string ALWAYS_ONLINE_URL = "https://raw.githubusercontent.com/pradt2/always-online-stun/master/valid_ipv4s.txt";

        public async IAsyncEnumerable<string> GetAvailableAsync()
        {
            var http = new HttpClient();
            using (var responseStream = await http.GetStreamAsync(ALWAYS_ONLINE_URL))
            using (var responseReader = new StreamReader(responseStream))
            {
                string? line;
                while ((line = await responseReader.ReadLineAsync()) is not null)
                {
                    yield return $"stun://{line}";
                }
            }
        }
    }
}
