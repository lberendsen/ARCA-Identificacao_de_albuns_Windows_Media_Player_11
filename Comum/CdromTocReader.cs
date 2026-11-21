using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace ARCA.Interop
{
    public static class CdromTocReader
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        // Sem compartilhar escrita, a abertura falha sempre que outro programa (o
        // WMP, o Explorer) está com a unidade aberta — e o DiscID virava "UnknownDisc".
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_CDROM_READ_TOC = 0x00024000;

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACK_DATA
        {
            public byte Reserved;
            public byte Control_Adr;
            public byte TrackNumber;
            public byte Reserved1;
            public byte Address_0;
            public byte Address_1;
            public byte Address_2;
            public byte Address_3;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CDROM_TOC
        {
            public ushort Length;
            public byte FirstTrack;
            public byte LastTrack;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)]
            public TRACK_DATA[] TrackData;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr SecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            out CDROM_TOC lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static CDROM_TOC GetToc(char driveLetter)
        {
            string drivePath = string.Format(@"\\.\{0}:", driveLetter);
            IntPtr hDrive = CreateFile(drivePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            
            if (hDrive == new IntPtr(-1))
            {
                throw new Exception("Não foi possível acessar a unidade " + driveLetter + ": (Vazia ou em uso exclusivo).");
            }

            try
            {
                CDROM_TOC toc;
                uint bytesReturned;
                bool success = DeviceIoControl(hDrive, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, out toc, (uint)Marshal.SizeOf(typeof(CDROM_TOC)), out bytesReturned, IntPtr.Zero);
                
                if (!success)
                {
                    throw new Exception("Falha ao ler a TOC (Table of Contents) da mídia.");
                }
                return toc;
            }
            finally
            {
                CloseHandle(hDrive);
            }
        }

        /// <summary>
        /// O mesmo ID de <see cref="GetDiscId"/>, calculado a partir do
        /// AudioCD.cue da área de trabalho, sem o disco no drive.
        ///
        /// Existe porque o ID lido na extração só vivia na memória: fechar o
        /// ARCA entre a extração e a aprovação fazia a aprovação pular o
        /// ledger. O CUE tem tudo o que o MD5 usa — primeira e última faixa,
        /// início de cada uma e o lead-out. As posições do CUE contam do LBA 0,
        /// e o TOC lido do drive vem em MSF absoluto, com os 2 s (150 quadros)
        /// iniciais; daí o +150.
        ///
        /// null quando o CUE não basta: sem lead-out, sem faixas, ou disco
        /// multissessão (o lead-out do CUE é o da sessão 1, e o TOC do drive
        /// termina na última sessão — os IDs não coincidiriam).
        /// </summary>
        public static string CalcularIdDoCue(string strCaminhoCue)
        {
            try
            {
                if (!System.IO.File.Exists(strCaminhoCue)) return null;
                string[] arrLinhas = System.IO.File.ReadAllLines(strCaminhoCue, Encoding.UTF8);

                List<int> lstNumeros = new List<int>();
                List<int> lstInicios = new List<int>();
                int intLeadOut = -1;
                bool blnEsperandoIndice = false;
                foreach (string strBruta in arrLinhas)
                {
                    string strLinha = strBruta.Trim();
                    Match objM;
                    if (Regex.IsMatch(strLinha, @"^REM\s+SESSION\s+0*([2-9]|\d{2,})\b", RegexOptions.IgnoreCase)) return null;
                    if ((objM = Regex.Match(strLinha, @"^TRACK\s+(\d+)\s", RegexOptions.IgnoreCase)).Success)
                    {
                        lstNumeros.Add(int.Parse(objM.Groups[1].Value));
                        blnEsperandoIndice = true;
                    }
                    else if (blnEsperandoIndice && (objM = Regex.Match(strLinha, @"^INDEX\s+01\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase)).Success)
                    {
                        lstInicios.Add(Quadros(objM));
                        blnEsperandoIndice = false;
                    }
                    else if ((objM = Regex.Match(strLinha, @"^REM\s+LEAD-OUT\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase)).Success)
                    {
                        intLeadOut = Quadros(objM);
                    }
                }
                if (lstNumeros.Count == 0 || lstInicios.Count != lstNumeros.Count || intLeadOut <= 0) return null;

                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("{0}-{1}-", lstNumeros[0], lstNumeros[lstNumeros.Count - 1]);
                foreach (int intInicio in lstInicios) sb.Append(Msf(intInicio + 150));
                sb.Append(Msf(intLeadOut + 150));

                using (MD5 md5 = MD5.Create())
                {
                    byte[] hashBytes = md5.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));
                    StringBuilder hex = new StringBuilder(hashBytes.Length * 2);
                    foreach (byte b in hashBytes) hex.AppendFormat("{0:X2}", b);
                    return hex.ToString().Substring(0, 16);
                }
            }
            catch { return null; }
        }

        private static int Quadros(Match objM)
        {
            return (int.Parse(objM.Groups[1].Value) * 60 + int.Parse(objM.Groups[2].Value)) * 75 + int.Parse(objM.Groups[3].Value);
        }

        /// <summary>Quadros absolutos no mesmo formato que GetDiscId usa: M, S e F em dois dígitos hexadecimais.</summary>
        private static string Msf(int intQuadros)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}", intQuadros / 4500, (intQuadros / 75) % 60, intQuadros % 75);
        }

        public static string GetDiscId(char driveLetter)
        {
            string drivePath = string.Format(@"\\.\{0}:", driveLetter);
            IntPtr hDrive = CreateFile(drivePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            
            if (hDrive == new IntPtr(-1))
            {
                throw new Exception("Não foi possível acessar a unidade " + driveLetter + ": (Vazia ou em uso exclusivo).");
            }

            try
            {
                CDROM_TOC toc;
                uint bytesReturned;
                bool success = DeviceIoControl(hDrive, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, out toc, (uint)Marshal.SizeOf(typeof(CDROM_TOC)), out bytesReturned, IntPtr.Zero);
                
                if (!success)
                {
                    throw new Exception("Falha ao ler a TOC (Table of Contents) da mídia.");
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("{0}-{1}-", toc.FirstTrack, toc.LastTrack);

                for (int i = 0; i <= toc.LastTrack - toc.FirstTrack + 1; i++) 
                {
                    var td = toc.TrackData[i];
                    sb.AppendFormat("{0:X2}{1:X2}{2:X2}", td.Address_1, td.Address_2, td.Address_3);
                }

                using (MD5 md5 = MD5.Create())
                {
                    byte[] hashBytes = md5.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));
                    StringBuilder hex = new StringBuilder(hashBytes.Length * 2);
                    foreach (byte b in hashBytes)
                        hex.AppendFormat("{0:X2}", b);
                    
                    return hex.ToString().Substring(0, 16);
                }
            }
            finally
            {
                CloseHandle(hDrive);
            }
        }
    }
}
