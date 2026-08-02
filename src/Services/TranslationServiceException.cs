namespace AITranslator.Services;

public sealed class TranslationServiceException : Exception
{
    public TranslationServiceException(string message) : base(message)
    {
    }

    public TranslationServiceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}