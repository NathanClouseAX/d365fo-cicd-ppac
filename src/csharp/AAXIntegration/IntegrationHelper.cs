using System;
using System.Net;
using System.Threading.Tasks;

namespace AAXIntegration
{
    /// <summary>
    /// Sample C# class library referenced by X++ via CLR interop.
    /// X++ calls into this assembly for operations better suited to .NET
    /// (HTTP clients, JSON parsing, complex integrations).
    /// </summary>
    public class IntegrationHelper
    {
        public static string BuildRequestPayload(string entityName, string action)
        {
            return $"{{\"entity\":\"{entityName}\",\"action\":\"{action}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
        }
    }
}
