namespace Subverse.Server
{
    internal class PgpKeyProvider : IPgpKeyProvider
    {
        private const string DEFAULT_PUBLICKEY_PATH = "./server/conf/hub-public.asc";
        private const string DEFAULT_PRIVATEKEY_PATH = "./server/conf/hub-private.asc";

        private readonly IConfiguration _configuration;

        public PgpKeyProvider(IConfiguration configuration) 
        {
            _configuration = configuration;
        }

        public FileInfo GetPublicKeyFile()
        {
            var pgpKeyFile = new FileInfo(_configuration.GetRequiredSection("Privacy")
                .GetValue<string>("PublicKeyPath") ?? DEFAULT_PUBLICKEY_PATH);
            return pgpKeyFile;
        }

        public FileInfo GetPrivateKeyFile()
        {
            var pgpKeyFile = new FileInfo(_configuration.GetRequiredSection("Privacy")
                .GetValue<string>("PrivateKeyPath") ?? DEFAULT_PRIVATEKEY_PATH);
            return pgpKeyFile;
        }

        public string GetPrivateKeyPassPhrase()
        {
            var pgpKeyPassPhrase = _configuration.GetRequiredSection("Privacy")
                .GetValue<string>("PrivateKeyPassPhrase") ?? string.Empty;
            return pgpKeyPassPhrase;
        }
    }
}
