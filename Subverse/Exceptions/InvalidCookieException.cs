namespace Subverse.Exceptions
{
    [Serializable]
    public class InvalidCookieException : Exception
    {
        public InvalidCookieException()
        {
        }

        public InvalidCookieException(string? message) : base(message)
        {
        }

        public InvalidCookieException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
