namespace Subverse.Server
{
    internal class PgpKeyProvider : IPgpKeyProvider
    {
        private const string DEFAULT_PGP_PATH = "./conf/publicKey.asc";

        private readonly IConfiguration _configuration;

        public PgpKeyProvider(IConfiguration configuration) 
        {
            _configuration = configuration;
        }

        public FileInfo GetFile()
        {
            var pgpKeyFile = new FileInfo(_configuration.GetRequiredSection("Privacy")
                .GetValue<string>("PGPKeyPath") ?? DEFAULT_PGP_PATH);
            return pgpKeyFile;
        }

        public string GetPassPhrase()
        {
            var pgpKeyPassPhrase = _configuration.GetRequiredSection("Privacy")
                .GetValue<string>("PGPKeyPassPhrase") ?? string.Empty;
            return pgpKeyPassPhrase;
        }
    }
}
