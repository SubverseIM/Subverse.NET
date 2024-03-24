namespace Subverse.Server
{
    internal interface IPgpKeyProvider
    {
        FileInfo GetPublicKeyFile();
        FileInfo GetPrivateKeyFile();
        string? GetPrivateKeyPassPhrase();
    }
}
