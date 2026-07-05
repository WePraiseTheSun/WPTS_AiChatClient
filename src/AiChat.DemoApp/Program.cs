using System;
using System.Drawing;
using System.Windows.Forms;

namespace AiChat.DemoApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    /// <summary>
    /// Minimal host demonstrating how to embed the control in an existing WinForms app:
    /// instantiate, optionally set DataDirectory, add to Controls. That's the whole API.
    /// </summary>
    internal sealed class MainForm : Form
    {
        public MainForm()
        {
            Text = "AI Chat — Demo Host";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 860);
            MinimumSize = new Size(760, 520);

            var chat = new AiChat.Control.ChatControl
            {
                // DataDirectory = @"C:\ProgramData\MyApp\AiChat" // optional override
            };
            Controls.Add(chat);
        }
    }
}
