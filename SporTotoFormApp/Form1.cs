using SporTotoFormApp.Interfaces;
using SporTotoFormApp.Services;
using System.Diagnostics;

namespace SporTotoFormApp
{
    public partial class Form1 : Form, ITestView
    {

        public int ProgressBarValue
        {
            get => progressBar1.Value;
            set
            {
                if (progressBar1.InvokeRequired)
                {
                    progressBar1.Invoke(new Action(() =>
                        progressBar1.Value = value));
                }
                else
                {
                    progressBar1.Value = value;
                }
                label1.Text = value.ToString();
            }
        }

        public int ProgressBarMaxValue
        {
            get => progressBar1.Maximum;
            set
            {
                if (progressBar1.InvokeRequired)
                {
                    progressBar1.Invoke(new Action(() =>
                        progressBar1.Maximum = value));
                }
                else
                {
                    progressBar1.Maximum = value;
                }
            }
        }



        public Form1()
        {
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            button1.Enabled = false;
            progressBar1.Minimum = 0;

            MoneyFilterService moneyFilterService = new MoneyFilterService(this, Convert.ToInt32(textBox1.Text));

            await moneyFilterService.Run();
            button1.Enabled = true;

        }

        public void Log(string message)
        {
            Log(message, Color.Gainsboro);
        }

        public void Log(string message, Color color)
        {
            if (rtb_log.InvokeRequired)
            {
                rtb_log.Invoke(new Action(() => Log(message, color)));
            }
            else
            {
                rtb_log.SelectionStart = rtb_log.TextLength;
                rtb_log.SelectionLength = 0;
                rtb_log.SelectionColor = color;
                rtb_log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                rtb_log.ScrollToCaret();
            }
        }

        private void rtb_log_TextChanged(object sender, EventArgs e)
        {

        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }

        private void button2_Click(object sender, EventArgs e)
        {
            string path = Path.Combine(Application.StartupPath, "BestScoreCoupon.txt");

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
    }
}
