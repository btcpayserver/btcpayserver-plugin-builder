#nullable disable
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit.Abstractions;

namespace PluginBuilder.Tests
{
    public class XUnitLogger : ILogger, ILoggerProvider, ILoggerFactory
    {
        public XUnitLogger(ITestOutputHelper log)
        {
            this.log = log;
        }
        string category;
        public ITestOutputHelper log;

        public XUnitLogger(string category, ITestOutputHelper log)
        {
            this.category = category;
            this.log = log;
        }
        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new XUnitLogger(categoryName, log);
        }
        public ILogger<T> CreateLogger<T>()
        {
            return new Logger<T>(this);
        }

        public void Dispose()
        {

        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            log.WriteLine($"[{Simplified(category)}] {Simplified(logLevel)}: {formatter(state, exception)}");
            if (exception is Exception)
            {
                log.WriteLine($"Exception: {exception}");
            }
        }

        private string Simplified(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Information:
                    return "Info";
                case LogLevel.Warning:
                    return "Warn";
                default:
                    return logLevel.ToString();
            }
        }

        private string Simplified(string category)
        {
            return category.Split('.').Last();
            //return category;
        }

        public void AddProvider(ILoggerProvider provider)
        {

        }
    }
}
