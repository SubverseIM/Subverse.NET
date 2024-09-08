namespace Subverse.Server
{
    internal static class EnumerableExtensions
    {
        public static IEnumerable<U> FlattenWithLock<T, U>(this IEnumerable<T> source)
            where T : IEnumerable<U>
        {
            foreach (T inner in source)
            {
                lock (inner)
                {
                    foreach (U elem in inner)
                    {
                        yield return elem;
                    }
                }
            }
        }
    }
}
