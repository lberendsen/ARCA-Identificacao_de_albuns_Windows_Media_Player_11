using System;
using System.Runtime.InteropServices;

namespace ARCA.Identificar
{
    /// <summary>
    /// WM/TrackNumber como o WMP grava: DWORD. O EditorAsf (Comum\) só grava
    /// texto, GUID e imagem, e não toca nele (num rip o número já vem certo).
    /// Mesmo motor (Windows Media Format SDK, wmvcore.dll), arquivo já fechado
    /// pelo EditorAsf.
    /// </summary>
    internal static class NumeroAsf
    {
        private const int TIPO_DWORD = 0;

        [DllImport("wmvcore.dll", ExactSpelling = true)]
        private static extern int WMCreateEditor(out IWMMetadataEditor editor);

        [ComImport, Guid("96406BD9-2B2B-11D3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMMetadataEditor
        {
            void Open([In, MarshalAs(UnmanagedType.LPWStr)] string arquivo);
            void Close();
            void Flush();
        }

        // wmsdkidl.h, IWMHeaderInfo: só a ordem da vtable importa até SetAttribute.
        [ComImport, Guid("96406BDA-2B2B-11D3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMHeaderInfo
        {
            void GetAttributeCount(ushort stream, out ushort qtd);
            void GetAttributeByIndex(ushort indice, ref ushort stream, IntPtr nome, ref ushort nomeLen, out int tipo, IntPtr valor, ref ushort valorLen);
            void GetAttributeByName(ref ushort stream, [In, MarshalAs(UnmanagedType.LPWStr)] string nome, out int tipo, IntPtr valor, ref ushort valorLen);
            void SetAttribute(ushort stream, [In, MarshalAs(UnmanagedType.LPWStr)] string nome, int tipo, IntPtr valor, ushort tamanho);
        }

        /// <summary>
        /// O número da faixa nos dois atributos que o WMP grava ao extrair um CD:
        /// WM/TrackNumber (1, 2, 3…) e o antigo WM/Track (0, 1, 2…).
        /// </summary>
        public static void DefinirNumeroDaFaixa(string strArquivo, uint uintNumero)
        {
            Definir(strArquivo, new[] { "WM/TrackNumber", "WM/Track" }, new[] { uintNumero, uintNumero - 1 });
        }

        public static void DefinirDword(string strArquivo, string strNome, uint uintValor)
        {
            Definir(strArquivo, new[] { strNome }, new[] { uintValor });
        }

        private static void Definir(string strArquivo, string[] vetNomes, uint[] vetValores)
        {
            IWMMetadataEditor ed;
            int hr = WMCreateEditor(out ed);
            if (hr < 0 || ed == null) throw new InvalidOperationException("WMCreateEditor devolveu 0x" + hr.ToString("X8"));
            IntPtr p = Marshal.AllocHGlobal(4);
            try
            {
                ed.Open(strArquivo);
                for (int i = 0; i < vetNomes.Length; i++)
                {
                    Marshal.WriteInt32(p, unchecked((int)vetValores[i]));
                    ((IWMHeaderInfo)ed).SetAttribute(0, vetNomes[i], TIPO_DWORD, p, 4);
                }
                ed.Flush();
            }
            finally
            {
                Marshal.FreeHGlobal(p);
                try { ed.Close(); } catch { }
                Marshal.ReleaseComObject(ed);
            }
        }
    }
}
