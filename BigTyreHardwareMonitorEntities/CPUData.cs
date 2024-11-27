namespace BigTyreHardwareMonitorEntities
{
    public class CPUData(string name)
    {
        public string Name { get; } = name;
        public Dictionary<int, CPUCoreData> Cores { get; set; } = [];
    }
}
