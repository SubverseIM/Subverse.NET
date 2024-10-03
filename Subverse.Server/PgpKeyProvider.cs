namespace Subverse.Server
{
    internal class PgpKeyProvider : IPgpKeyProvider
    {
        private const string DEFAULT_PUBLICKEY_PATH = "server/conf/hub-public.asc";
        private const string DEFAULT_PRIVATEKEY_PATH = "server/conf/hub-private.asc";
        private const string DEFAULT_PRIVATEKEY_PASSWD = "#FreeTheInternet";

        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;

        public PgpKeyProvider(IConfiguration configuration, IHostEnvironment environment) 
        {
            _configuration = configuration;
            _environment = environment;
        }

        public FileInfo GetPublicKeyFile()
        {
            var userPath = _configuration.GetSection("Privacy")
                .GetValue<string>("PublicKeyPath") ?? DEFAULT_PUBLICKEY_PATH;

            var publicKeyFile = new FileInfo(Path.IsPathFullyQualified(userPath) ? userPath :
                Path.Combine(_environment.ContentRootPath, userPath));

            return publicKeyFile;
        }

        public FileInfo GetPrivateKeyFile()
        {
            var userPath = _configuration.GetSection("Privacy")
                .GetValue<string>("PrivateKeyPath") ?? DEFAULT_PRIVATEKEY_PATH;

            var privateKeyFile = new FileInfo(Path.IsPathFullyQualified(userPath) ? userPath :
                Path.Combine(_environment.ContentRootPath, userPath));

            return privateKeyFile;
        }

        public string? GetPrivateKeyPassPhrase()
        {
            var pgpKeyPassPhrase = _configuration.GetSection("Privacy")
                .GetValue<string>("PrivateKeyPassPhrase") ?? DEFAULT_PRIVATEKEY_PASSWD;

            return pgpKeyPassPhrase;
        }
    }
}
