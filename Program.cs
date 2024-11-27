using BigTyreHardwareMonitorEntities;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System.Text;
using System.Text.RegularExpressions;

var interval = TimeSpan.FromSeconds(15);

// Build a configuration
var configBuilder = new ConfigurationBuilder();
configBuilder.AddCommandLine(args);
#if DEBUG
configBuilder.AddUserSecrets<Program>();
#endif
var config = configBuilder.Build();

// Build the app settings
var settings = new AppSettings();
config.Bind(settings);

var apiUrlString = settings.ApiUrl ?? throw new Exception("API URL not configured.");
var apiUrl = new Uri(apiUrlString);

const string httpClientName = "Default";

// Build services
var services = new ServiceCollection();
services.AddHttpClient(httpClientName, client =>
{
    client.BaseAddress = apiUrl;
}).AddHttpMessageHandler(() => new LoggingHandler());

var serviceProvider = services.BuildServiceProvider();
var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();



var computer = new Computer
{
    IsCpuEnabled = true // Enable CPU monitoring
};

var tjMaxRegex = new Regex("^CPU Core #([0-9]+) Distance to TjMax$", RegexOptions.IgnoreCase);
var cpuTempRegex = new Regex("^CPU Core #([0-9]+)$", RegexOptions.IgnoreCase);

var hostName = Environment.MachineName;

#if DEBUG
hostName += "-debug";
#endif

Dictionary<int, CPUData> cpus = [];

while (true)
{
    try
    {
        computer.Open();

        while (true)
        {
            Console.Clear();

            int cpuNumber = 0;
            foreach (var hardware in computer.Hardware)
            {
                var hardwareName = hardware.Name;
                var hardwareType = hardware.HardwareType;

                if (hardwareType != HardwareType.Cpu)
                    continue;

                cpuNumber++;

                if (!cpus.TryGetValue(cpuNumber, out var cpu))
                {
                    cpu = new(hardwareName);
                    cpus[cpuNumber] = cpu;
                }

                // Console.WriteLine($"Hardware: {hardware.Name}");

                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature)
                        continue;

                    var value = sensor.Value;
                    if (value is null)
                    {
#if DEBUG
                        value = 10;
#else
                continue;
#endif
                    }

                    var sensorValue = value.Value;

                    var sensorName = sensor.Name;

                    // Console.WriteLine($"Sensor: {sensorName}, Value: {sensor.Value}°C");

                    {
                        var tjMaxRegexMatch = tjMaxRegex.Match(sensorName);
                        if (tjMaxRegexMatch.Success)
                        {
                            var coreNumber = int.Parse(tjMaxRegexMatch.Groups[1].Value);
                            if (!cpu.Cores.TryGetValue(coreNumber, out var core))
                            {
                                core = new();
                                cpu.Cores[coreNumber] = core;
                            }
                            core.DistanceToTJMax = new(sensorValue, DateTimeOffset.Now);
                            continue;
                        }
                    }

                    {
                        var cpuTempRegexMatch = cpuTempRegex.Match(sensorName);
                        if (cpuTempRegexMatch.Success)
                        {
                            var coreNumber = int.Parse(cpuTempRegexMatch.Groups[1].Value);
                            if (!cpu.Cores.TryGetValue(coreNumber, out var core))
                            {
                                core = new();
                                cpu.Cores[coreNumber] = core;
                            }
                            core.Temperature = new(sensorValue, DateTimeOffset.Now);
                            continue;
                        }
                    }
                }
            }

            await PushMetricsAsync();

            await Task.Delay(interval);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"An error occurred: {ex.Message}");
    }
}

//computer.Close();

async Task PushMetricsAsync()
{
    var request = new PushMetricsRequest(hostName, cpus);
    var requestAsJson = JsonConvert.SerializeObject(request, Formatting.Indented);
    //Console.WriteLine(requestAsJson);


    var httpClient = httpClientFactory.CreateClient(httpClientName);
    var content = new StringContent(requestAsJson, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("hardware-data", content);

    // Handle the response
    if (response.IsSuccessStatusCode)
    {
        Console.WriteLine("Request succeeded!");
    }
    else
    {
        Console.WriteLine($"Request failed with status code: {response.StatusCode}");
    }
}


public class LoggingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Log the absolute URL here
        var method = request.Method;
        Console.WriteLine($"Sending {method} request to {request.RequestUri}");

        // Continue with the request
        return await base.SendAsync(request, cancellationToken);
    }
}

internal class AppSettings
{
    public string? ApiUrl { get; set; }
}