using System;
using System.Windows.Forms;

namespace VSRS
{
    internal static class Program
    {
        // WinForms/檔案選擇對話框需要 STA；此處只初始化 UI，不執行磁碟修改。
        // ThreadException 是 UI 最後防線；正常作業錯誤應由 RunBusyAsync 顯示及解鎖。
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.Message, "VSRS 錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
        }
    }
}
