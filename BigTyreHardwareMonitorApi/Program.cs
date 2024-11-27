using BigTyreHardwareMonitorEntities;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

var metricUpdateTimes = new MetricUpdateTimes();

builder.Services.AddSingleton<MetricUpdateTimes>();
builder.Services.AddHostedService<StaleMetricClearingService>();

// Add services to the container.

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();
app.UseHttpMetrics();


app.MapPost("/hardware-data", ([FromBody] PushMetricsRequest data) =>
{
    Console.WriteLine("Received data");
    Console.WriteLine(JsonConvert.SerializeObject(data, Formatting.Indented));

    var hostName = data.HostName;
    foreach (var pair in data.CPUs)
    {
        var cpuNumber = pair.Key;
        var cpuData = pair.Value;

        foreach (var corePair in cpuData.Cores)
        {
            var coreNumber = corePair.Key;
            var coreData = corePair.Value;


            var coreTemperature = coreData.Temperature;
            if (coreTemperature is not null)
            {
                AppMetrics.cpuTempGauge.WithLabels([hostName, cpuNumber.ToString(), coreNumber.ToString()]).Set(coreTemperature.Value);
                metricUpdateTimes.SetUpdateTime(hostName, coreTemperature.Time);
            }

            var coreDistanceToTjMax = coreData.DistanceToTJMax;
            if (coreDistanceToTjMax is not null)
            {

                AppMetrics.cpuDistanceToTJMaxGauge.WithLabels([hostName, cpuNumber.ToString(), coreNumber.ToString()]).Set(coreDistanceToTjMax.Value);
                metricUpdateTimes.SetUpdateTime(hostName, coreDistanceToTjMax.Time);
            }
        }
    }

});

app.MapMetrics();

app.Run();

static class AppMetrics
{
    public static Gauge cpuTempGauge = Metrics.CreateGauge(
        "cpu_core_temperature",
        "Tracks the temperature in Celcius of cores in device CPUs.",
        new GaugeConfiguration { LabelNames = ["hostname", "cpu", "core"] }
    );

    public static Gauge cpuDistanceToTJMaxGauge = Metrics.CreateGauge(
        "cpu_core_distance_to_tj_max",
        "Tracks the degrees in Celcius left until a CPU core hits its maximum allowable temperature.",
        new GaugeConfiguration { LabelNames = ["hostname", "cpu", "core"] }
    );
}


class StaleMetricClearingService(MetricUpdateTimes metricUpdateTimes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cancellationToken = stoppingToken;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ClearStaleMetrics(AppMetrics.cpuTempGauge);
                ClearStaleMetrics(AppMetrics.cpuDistanceToTJMaxGauge);
            }
            catch (Exception ex)
            {
                var errorMessage = ex.Message;
                Console.Error.Write($"An error occurred in {nameof(StaleMetricClearingService)}: {errorMessage}");
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    private void ClearStaleMetrics(Gauge gauge)
    {
        var cutoffTime = DateTimeOffset.Now - TimeSpan.FromMinutes(5);

        var labelValues = gauge.GetAllLabelValues().ToList();
        
        var hostnames = labelValues.Select(sample => sample[0]).Distinct().ToList();

        foreach (var hostname in hostnames)
        {
            var updateTime = metricUpdateTimes.GetUpdateTime(hostname);
            if (updateTime is not null && updateTime > cutoffTime)
                continue;

            var hostLabels = labelValues.Where(c => c[0] == hostname).ToList();
            foreach (var labelValue in hostLabels)
            {
                gauge.RemoveLabelled(labelValue);
            }
        }
    }
}


class MetricUpdateTimes
{
    /// <summary>
    /// Dictionary of last update time by hostname
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _values = [];
    private readonly object _valuesLock = new();

    public DateTimeOffset? GetUpdateTime(string hostName)
    {
        lock (_valuesLock)
        {
            if (_values.TryGetValue(hostName, out var value))
                return value;
        }
        return null;
    }

    public void SetUpdateTime(string hostName, DateTimeOffset time)
    {
        lock (_valuesLock)
        {
            _values[hostName] = time;
        }
    }
}