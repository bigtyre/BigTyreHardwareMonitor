namespace BigTyreHardwareMonitorEntities
{

    public record PushMetricsRequest(string HostName, Dictionary<int, CPUData> CPUs);
}
