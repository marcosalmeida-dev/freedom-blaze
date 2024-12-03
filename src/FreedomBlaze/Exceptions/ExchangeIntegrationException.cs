namespace FreedomBlaze.Exceptions;

public class ExchangeIntegrationException : Exception
{
    public ExchangeIntegrationException(string exchangeName) : base()
    {
        ExchangeName = exchangeName;
    }

    public string ExchangeName { get; set; }
}
