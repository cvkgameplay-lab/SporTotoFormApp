namespace SporTotoFormApp
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            button1 = new Button();
            progressBar1 = new ProgressBar();
            rtb_log = new RichTextBox();
            label1 = new Label();
            label2 = new Label();
            textBox1 = new TextBox();
            button2 = new Button();
            SuspendLayout();
            // 
            // button1
            // 
            button1.Location = new Point(568, 57);
            button1.Name = "button1";
            button1.Size = new Size(212, 46);
            button1.TabIndex = 0;
            button1.Text = "ÇALIŞTIR";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // progressBar1
            // 
            progressBar1.Location = new Point(12, 28);
            progressBar1.Name = "progressBar1";
            progressBar1.Size = new Size(768, 23);
            progressBar1.TabIndex = 1;
            // 
            // rtb_log
            // 
            rtb_log.BackColor = Color.FromArgb(30, 30, 30);
            rtb_log.BorderStyle = BorderStyle.None;
            rtb_log.Font = new Font("Consolas", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 162);
            rtb_log.ForeColor = Color.Gainsboro;
            rtb_log.Location = new Point(13, 110);
            rtb_log.Margin = new Padding(4);
            rtb_log.Name = "rtb_log";
            rtb_log.ReadOnly = true;
            rtb_log.Size = new Size(767, 255);
            rtb_log.TabIndex = 69;
            rtb_log.Text = "";
            rtb_log.TextChanged += rtb_log_TextChanged;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(13, 54);
            label1.Name = "label1";
            label1.Size = new Size(0, 15);
            label1.TabIndex = 70;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(375, 73);
            label2.Name = "label2";
            label2.Size = new Size(70, 15);
            label2.TabIndex = 71;
            label2.Text = "Kolon Sayısı";
            // 
            // textBox1
            // 
            textBox1.Location = new Point(451, 70);
            textBox1.Name = "textBox1";
            textBox1.Size = new Size(100, 23);
            textBox1.TabIndex = 72;
            textBox1.TextChanged += textBox1_TextChanged;
            // 
            // button2
            // 
            button2.Location = new Point(13, 372);
            button2.Name = "button2";
            button2.Size = new Size(767, 33);
            button2.TabIndex = 73;
            button2.Text = "Tahmin Dosyasını Aç";
            button2.UseVisualStyleBackColor = true;
            button2.Click += button2_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(803, 409);
            Controls.Add(button2);
            Controls.Add(textBox1);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(rtb_log);
            Controls.Add(progressBar1);
            Controls.Add(button1);
            Name = "Form1";
            Text = "SporTotoFormApp";
            Load += Form1_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button button1;
        private ProgressBar progressBar1;
        private RichTextBox rtb_log;
        private Label label1;
        private Label label2;
        private TextBox textBox1;
        private Button button2;
    }
}
