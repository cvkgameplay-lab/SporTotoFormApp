namespace SporTotoFormApp.Interfaces
{
    public interface ITestView
    {
        int ProgressBarValue { get; set; }
        int ProgressBarMaxValue { get; set; }
        void Log(string message);
        void Log(string message, Color color);
    }
}
