using System;

namespace BTCPayServer.Plugins.SatfluxTickets
{
    public class SimpleTicketSalesApiException : Exception
    {
        public SimpleTicketSalesApiException(string message) : base(message)
        {
        }
    }
}
