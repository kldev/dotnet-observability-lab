using Dapper;
using Npgsql;

namespace ObservabilityLab.Hosting;

public static class DataAccessExtensions
{
    /// <summary>One NpgsqlDataSource + Dapper, nothing more.</summary>
    public static IHostApplicationBuilder AddDataAccess(this IHostApplicationBuilder builder)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new OrderStatusHandler());

        builder.Services.AddSingleton(_ =>
        {
            var dataSource = new NpgsqlDataSourceBuilder(
                builder.Configuration.GetConnectionString("Orders")
                    ?? throw new InvalidOperationException("ConnectionStrings:Orders is missing")
            );
            dataSource.Name = "orders"; // pool name used in Npgsql metric labels
            // Span name = the SQL itself (shortened), e.g. "SELECT pg_sleep(@seconds)" instead of just "postgresql".
            dataSource.ConfigureTracing(o =>
                o.ConfigureCommandSpanNameProvider(cmd => SqlSpanName(cmd.CommandText))
            );
            return dataSource.Build();
        });

        return builder;
    }

    private static string SqlSpanName(string sql)
    {
        var oneLine = string.Join(
            ' ',
            sql.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
        );
        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }
}
