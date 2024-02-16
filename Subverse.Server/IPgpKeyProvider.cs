namespace Subverse.Server
{
    internal interface IPgpKeyProvider
    {
        FileInfo GetFile();
        string GetPassPhrase();
    }
}
