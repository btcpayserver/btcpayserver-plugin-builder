using System.Threading.Channels;
using Dapper;
using PluginBuilder.Services;

namespace PluginBuilder
{
    public class BuildOutputCapture : IOutputCapture, IDisposable
    {
        public BuildOutputCapture(FullBuildId fullBuildId, DBConnectionFactory connectionFactory)
        {
            FullBuildId = fullBuildId;
            ConnectionFactory = connectionFactory;
            _ = SaveLoop();
        }

        public FullBuildId FullBuildId { get; }
        public DBConnectionFactory ConnectionFactory { get; }
        Channel<string> lines = Channel.CreateUnbounded<string>();
        public void AddLine(string line)
        {
            lines.Writer.TryWrite(line);
        }

        async Task SaveLoop()
        {
            while (await lines.Reader.WaitToReadAsync())
            {
                List<string> rows = new List<string>();
                while (lines.Reader.TryRead(out var l))
                    rows.Add(l);
                using var conn = await ConnectionFactory.Open();
                await conn.ExecuteAsync("INSERT INTO builds_logs VALUES (@pluginSlug, @buildId, @log)",
                    rows.Select(row =>
                    new
                    {
                        pluginSlug = FullBuildId.PluginSlug.ToString(),
                        buildId = FullBuildId.BuildId,
                        log = row
                    }).ToArray());
            }
        }

        public void Dispose()
        {
            lines.Writer.TryComplete();
        }
    }
}
