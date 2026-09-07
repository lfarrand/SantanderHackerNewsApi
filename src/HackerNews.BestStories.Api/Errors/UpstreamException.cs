namespace HackerNews.BestStories.Api.Errors;

public class UpstreamException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class UpstreamTimeoutException(string message, Exception? innerException = null)
    : UpstreamException(message, innerException);
