using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ARCA.Modelo;

namespace ARCA.Interop
{
    /// <summary>
    /// Leitor óptico de baixo nível via SCSI Pass-Through Direct (SPTI).
    /// Permite comunicação direta com a cabeça de leitura óptica, bypassando drivers intermediários
    /// e fornecendo controle fino sobre LBA, timeouts, buffer halving e descarte de cache.
    /// </summary>
    public class SptiDiscReader : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
        private const uint IOCTL_CDROM_READ_TOC = 0x00024000;
        private const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;
        private const byte SCSI_IOCTL_DATA_IN = 1;

        #region Estruturas SPTI Win32 (Alinhadas em Byte)

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SCSI_PASS_THROUGH_DIRECT
        {
            public ushort Length;
            public byte ScsiStatus;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte CdbLength;
            public byte SenseInfoLength;
            public byte DataIn;
            public byte Reserved1;
            public byte Reserved2;
            public byte Reserved3;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public IntPtr DataBuffer;
            public uint SenseInfoOffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Cdb;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
        {
            public SCSI_PASS_THROUGH_DIRECT sptd;
            public uint Filler;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] SenseBuf;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct CDROM_READ_TOC_EX
        {
            public byte Format;
            public byte SessionTrack;
            public byte Reserved1;
            public byte Reserved2;
        }

        #endregion

        #region P/Invoke Kernel32

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER lpInBuffer,
            uint nInBufferSize,
            ref SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            out CdromTocReader.CDROM_TOC lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref CDROM_READ_TOC_EX lpInBuffer,
            uint nInBufferSize,
            [Out] byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        private IntPtr m_hDeviceHandle = new IntPtr(-1);
        private readonly AcquisitionLogger m_objLogger;

        public SptiDiscReader(AcquisitionLogger objLogger)
        {
            m_objLogger = objLogger;
        }

        public bool IsOpen
        {
            get { return m_hDeviceHandle != new IntPtr(-1) && m_hDeviceHandle != IntPtr.Zero; }
        }

        public bool AbrirUnidade(string strLetraDrive)
        {
            FecharUnidade();

            string strCaminhoDevice = string.Format(@"\\.\{0}:", strLetraDrive.TrimEnd(':', '\\', '/'));
            m_objLogger.RegistrarDiagnostico(string.Format("Tentando abrir handle direto para {0}...", strCaminhoDevice));

            m_hDeviceHandle = CreateFile(strCaminhoDevice, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (m_hDeviceHandle == new IntPtr(-1))
            {
                int intErroWin32 = Marshal.GetLastWin32Error();
                m_objLogger.RegistrarDiagnostico(string.Format("Abertura com GENERIC_WRITE falhou (Win32: {0}). Tentando GENERIC_READ...", intErroWin32));

                m_hDeviceHandle = CreateFile(strCaminhoDevice, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (m_hDeviceHandle == new IntPtr(-1))
                {
                    intErroWin32 = Marshal.GetLastWin32Error();
                    m_objLogger.RegistrarErro(string.Format("Não foi possível acessar a unidade {0}. Código Win32: {1}", strCaminhoDevice, intErroWin32));
                    return false;
                }
            }

            m_objLogger.RegistrarDiagnostico(string.Format("Handle SPTI aberto com sucesso em {0}.", strCaminhoDevice));
            return true;
        }

        public void FecharUnidade()
        {
            if (IsOpen)
            {
                CloseHandle(m_hDeviceHandle);
                m_hDeviceHandle = new IntPtr(-1);
                m_objLogger.RegistrarDiagnostico("Handle SPTI fechado.");
            }
        }

        public bool ObterInquiryHardware(out string strVendor, out string strProduct, out string strRevision)
        {
            strVendor = "Desconhecido";
            strProduct = "Desconhecido";
            strRevision = "N/A";

            if (!IsOpen) return false;

            byte[] arrInqBuffer = new byte[36];
            GCHandle hPin = GCHandle.Alloc(arrInqBuffer, GCHandleType.Pinned);
            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arrInqBuffer.Length, hPin.AddrOfPinnedObject(), 10);
                sptd.sptd.CdbLength = 6;
                sptd.sptd.Cdb[0] = 0x12; // INQUIRY
                sptd.sptd.Cdb[4] = (byte)arrInqBuffer.Length;

                uint dwBytesRetornados;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                bool blnOk = DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero);

                if (blnOk && sptd.sptd.ScsiStatus == 0)
                {
                    strVendor = Encoding.ASCII.GetString(arrInqBuffer, 8, 8).Trim();
                    strProduct = Encoding.ASCII.GetString(arrInqBuffer, 16, 16).Trim();
                    strRevision = Encoding.ASCII.GetString(arrInqBuffer, 32, 4).Trim();
                    m_objLogger.RegistrarDiagnostico(string.Format("Hardware Detectado: [{0}] Modelo: [{1}] Firmware: [{2}]", strVendor, strProduct, strRevision));
                    return true;
                }
                return false;
            }
            finally
            {
                hPin.Free();
            }
        }

        public bool LerToc(out CdromTocReader.CDROM_TOC objToc)
        {
            objToc = new CdromTocReader.CDROM_TOC();
            if (!IsOpen) return false;

            uint dwBytesRetornados;
            uint dwSize = (uint)Marshal.SizeOf(typeof(CdromTocReader.CDROM_TOC));

            for (int intRetry = 0; intRetry < 5; intRetry++)
            {
                if (DeviceIoControl(m_hDeviceHandle, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, out objToc, dwSize, out dwBytesRetornados, IntPtr.Zero))
                {
                    m_objLogger.RegistrarDiagnostico(string.Format("TOC lida com sucesso: Primeira Faixa={0}, Última Faixa={1}", objToc.FirstTrack, objToc.LastTrack));
                    return true;
                }
                System.Threading.Thread.Sleep(300);
            }

            m_objLogger.RegistrarErro("Falha ao ler TOC da mídia óptica via IOCTL_CDROM_READ_TOC.");
            return false;
        }

        public bool LerCapacidade(out uint dwMaxLba, out uint dwBlockSize)
        {
            dwMaxLba = 0;
            dwBlockSize = 2048;
            if (!IsOpen) return false;

            byte[] arrCapBuffer = new byte[8];
            GCHandle hPin = GCHandle.Alloc(arrCapBuffer, GCHandleType.Pinned);
            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arrCapBuffer.Length, hPin.AddrOfPinnedObject(), 10);
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x25; // READ CAPACITY (10)

                uint dwBytesRetornados;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                bool blnOk = DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero);

                if (blnOk && sptd.sptd.ScsiStatus == 0)
                {
                    dwMaxLba = ((uint)arrCapBuffer[0] << 24) | ((uint)arrCapBuffer[1] << 16) | ((uint)arrCapBuffer[2] << 8) | (uint)arrCapBuffer[3];
                    dwBlockSize = ((uint)arrCapBuffer[4] << 24) | ((uint)arrCapBuffer[5] << 16) | ((uint)arrCapBuffer[6] << 8) | (uint)arrCapBuffer[7];
                    m_objLogger.RegistrarDiagnostico(string.Format("READ CAPACITY: MaxLBA={0}, BlockSize={1}", dwMaxLba, dwBlockSize));
                    return true;
                }
                return false;
            }
            finally
            {
                hPin.Free();
            }
        }

        /// <summary>Uma sessão do disco, como o índice completo a descreve.</summary>
        public sealed class TSessao
        {
            public int intNumero;
            /// <summary>Onde a sessão acaba (ponto A2, LBA). -1 se o drive não disse.</summary>
            public int intLeadOut = -1;
            /// <summary>Faixas da sessão: número, LBA inicial, é áudio.</summary>
            public readonly List<int[]> lstFaixas = new List<int[]>();
        }

        /// <summary>
        /// READ TOC formato 2 (índice completo, "Full TOC"): diz em que sessão
        /// cada faixa está e onde cada sessão acaba. É o único jeito de saber
        /// onde o áudio termina num CD com sessão de dados — o índice comum só
        /// dá o lead-out do disco inteiro, que fica depois dos dados.
        /// null se o drive não responder (aí vale a estimativa antiga).
        /// </summary>
        public List<TSessao> LerSessoes()
        {
            if (!IsOpen) return null;
            byte[] arr = new byte[4096];
            GCHandle hPin = GCHandle.Alloc(arr, GCHandleType.Pinned);
            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arr.Length, hPin.AddrOfPinnedObject(), 10);
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x43;                 // READ TOC/PMA/ATIP
                sptd.sptd.Cdb[1] = 0x02;                 // MSF
                sptd.sptd.Cdb[2] = 0x02;                 // formato 2: Full TOC
                sptd.sptd.Cdb[6] = 0x01;                 // a partir da sessão 1
                sptd.sptd.Cdb[7] = (byte)(arr.Length >> 8);
                sptd.sptd.Cdb[8] = (byte)(arr.Length & 0xFF);
                uint dwRet;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                if (!DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwRet, IntPtr.Zero)
                    || sptd.sptd.ScsiStatus != 0)
                {
                    m_objLogger.RegistrarDiagnostico("O drive não respondeu ao índice completo (READ TOC formato 2).");
                    return null;
                }

                int intTotal = Math.Min(arr.Length, ((arr[0] << 8) | arr[1]) + 2);
                SortedDictionary<int, TSessao> dic = new SortedDictionary<int, TSessao>();
                for (int p = 4; p + 11 <= intTotal; p += 11)
                {
                    int intSessao = arr[p];
                    int intAdr = arr[p + 1] >> 4, intControle = arr[p + 1] & 0x0F;
                    int intPonto = arr[p + 3];
                    if (intAdr != 1) continue;           // só descritores de posição
                    int intLba = (arr[p + 8] * 60 + arr[p + 9]) * 75 + arr[p + 10] - 150;
                    TSessao s;
                    if (!dic.TryGetValue(intSessao, out s)) { s = new TSessao { intNumero = intSessao }; dic[intSessao] = s; }
                    if (intPonto >= 1 && intPonto <= 99) s.lstFaixas.Add(new[] { intPonto, intLba, (intControle & 0x04) == 0 ? 1 : 0 });
                    else if (intPonto == 0xA2) s.intLeadOut = intLba;
                }
                List<TSessao> lst = new List<TSessao>(dic.Values);
                foreach (TSessao s in lst)
                    m_objLogger.RegistrarDiagnostico(string.Format("Sessão {0}: {1} faixa(s), lead-out no LBA {2}.", s.intNumero, s.lstFaixas.Count, s.intLeadOut));
                return lst.Count > 0 ? lst : null;
            }
            catch { return null; }
            finally { hPin.Free(); }
        }

        /// <summary>CD-TEXT tem no máximo 8 blocos × 256 pacotes de 18 bytes; 4 KB recusava discos inteiros.</summary>
        private const int TamanhoMaximoCdText = 4 + 8 * 256 * 18;

        public bool LerCdText(out string strAlbum, out string strArtista, out Dictionary<int, string> dicTitulos, out Dictionary<int, string> dicArtistas)
        {
            strAlbum = null;
            strArtista = null;
            dicTitulos = new Dictionary<int, string>();
            dicArtistas = new Dictionary<int, string>();

            if (!IsOpen) return false;

            byte[] arrCdTextBuffer = new byte[TamanhoMaximoCdText];
            GCHandle hPin = GCHandle.Alloc(arrCdTextBuffer, GCHandleType.Pinned);
            bool blnLeuComSucesso = false;
            int intLength = 0;

            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arrCdTextBuffer.Length, hPin.AddrOfPinnedObject(), 10);
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x43;
                sptd.sptd.Cdb[1] = 0x02;
                sptd.sptd.Cdb[2] = 0x05;
                sptd.sptd.Cdb[7] = (byte)((arrCdTextBuffer.Length >> 8) & 0xFF);
                sptd.sptd.Cdb[8] = (byte)(arrCdTextBuffer.Length & 0xFF);

                uint dwBytesRetornados;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                if (DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero) && sptd.sptd.ScsiStatus == 0)
                {
                    intLength = (arrCdTextBuffer[0] << 8) | arrCdTextBuffer[1];
                    blnLeuComSucesso = intLength > 2 && intLength + 2 <= arrCdTextBuffer.Length;
                }

                if (!blnLeuComSucesso)
                {
                    CDROM_READ_TOC_EX readTocEx = new CDROM_READ_TOC_EX();
                    readTocEx.Format = 0x05;
                    readTocEx.SessionTrack = 0;

                    if (DeviceIoControl(m_hDeviceHandle, IOCTL_CDROM_READ_TOC_EX, ref readTocEx, (uint)Marshal.SizeOf(typeof(CDROM_READ_TOC_EX)), arrCdTextBuffer, (uint)arrCdTextBuffer.Length, out dwBytesRetornados, IntPtr.Zero))
                    {
                        intLength = (arrCdTextBuffer[0] << 8) | arrCdTextBuffer[1];
                        blnLeuComSucesso = intLength > 2 && intLength + 2 <= arrCdTextBuffer.Length;
                    }
                }
            }
            finally
            {
                hPin.Free();
            }

            if (!blnLeuComSucesso) return false;

            int intDescartados;
            string strCodificacao;
            DecodificarCdText(arrCdTextBuffer, intLength + 2, out strAlbum, out strArtista, dicTitulos, dicArtistas, out intDescartados, out strCodificacao);
            if (intDescartados > 0)
                m_objLogger.RegistrarAviso(string.Format("CD-TEXT: {0} pacote(s) com CRC errado foram descartados; os textos que eles tocavam ficaram de fora em vez de saírem desalinhados.", intDescartados));
            m_objLogger.RegistrarDiagnostico(string.Format("CD-TEXT decodificado ({3}): Álbum='{0}', Artista='{1}', Faixas={2}", strAlbum, strArtista, dicTitulos.Count, strCodificacao));
            return strAlbum != null || strArtista != null || dicTitulos.Count > 0;
        }

        /// <summary>
        /// Decodifica o CD-TEXT (só o bloco 0, o idioma principal) a partir da
        /// resposta do READ TOC formato 5: 4 bytes de cabeçalho e pacotes de
        /// 18 bytes — tipo, faixa, sequência, bloco/posição, 12 bytes de texto
        /// e CRC-16.
        ///
        /// - Cada pacote tem o CRC conferido. Pacote ruim é descartado, e o
        ///   texto que ele tocava não sai — o pacote seguinte diz em que faixa
        ///   e em que posição do texto começa, então a leitura ressincroniza
        ///   em vez de desalinhar todos os títulos dali em diante.
        /// - A codificação vem do pacote de informações (0x8F, primeiro byte
        ///   de dados): 0x00 Latin-1, 0x01 ASCII, 0x80 Shift-JIS (japonês),
        ///   0x81 coreano, 0x82 chinês. Nas de dois bytes, o texto termina em
        ///   dois zeros, não em um — um zero sozinho é metade de um caractere.
        /// - TAB ("\t", ou "\t\t" nas de dois bytes) quer dizer "igual à faixa
        ///   anterior".
        ///
        /// Público e estático para poder ser testado sem drive.
        /// </summary>
        public static void DecodificarCdText(byte[] arr, int intTotal, out string strAlbum, out string strArtista,
                                             Dictionary<int, string> dicTitulos, Dictionary<int, string> dicArtistas,
                                             out int intDescartados, out string strCodificacao)
        {
            strAlbum = null;
            strArtista = null;
            intDescartados = 0;
            intTotal = Math.Min(intTotal, arr.Length);

            // Salvaguarda: se NENHUM pacote passar no CRC, o drive não faz a conta
            // padrão (ou a entrega de outro jeito) — descartar tudo perderia o
            // CD-TEXT inteiro por um detalhe do drive. Aí os pacotes valem como vêm.
            int intBons = 0, intRuins = 0;
            for (int p = 4; p + 18 <= intTotal; p += 18) { if (CrcPacoteOk(arr, p)) intBons++; else intRuins++; }
            bool blnIgnorarCrc = intBons == 0 && intRuins > 0;

            // Codificação: pacote 0x8F com "faixa" 0 do bloco 0, primeiro byte de dados.
            int intCodigo = 0x00;
            for (int p = 4; p + 18 <= intTotal; p += 18)
                if (arr[p] == 0x8F && (arr[p + 1] & 0x7F) == 0 && ((arr[p + 3] >> 4) & 0x07) == 0 && (blnIgnorarCrc || CrcPacoteOk(arr, p)))
                {
                    intCodigo = arr[p + 4];
                    break;
                }
            bool blnDuplo = intCodigo >= 0x80;
            Encoding enc;
            try
            {
                enc = intCodigo == 0x80 ? Encoding.GetEncoding(932)
                    : intCodigo == 0x81 ? Encoding.GetEncoding(949)
                    : intCodigo == 0x82 ? Encoding.GetEncoding(936)
                    : intCodigo == 0x01 ? Encoding.ASCII
                    : Encoding.GetEncoding(28591);
            }
            catch { enc = Encoding.GetEncoding(28591); }
            strCodificacao = intCodigo == 0x80 ? "Shift-JIS" : intCodigo == 0x81 ? "coreano" : intCodigo == 0x82 ? "chinês"
                           : intCodigo == 0x01 ? "ASCII" : "Latin-1";

            Dictionary<int, string> dicTitulosTodos = new Dictionary<int, string>();
            Dictionary<int, string> dicArtistasTodos = new Dictionary<int, string>();
            intDescartados += DecodificarTipo(arr, intTotal, 0x80, blnDuplo, enc, dicTitulosTodos, blnIgnorarCrc);
            intDescartados += DecodificarTipo(arr, intTotal, 0x81, blnDuplo, enc, dicArtistasTodos, blnIgnorarCrc);
            if (blnIgnorarCrc) strCodificacao += ", CRC do drive não conferível: pacotes aceitos como vieram";

            foreach (KeyValuePair<int, string> kv in dicTitulosTodos)
                if (kv.Key == 0) strAlbum = kv.Value; else dicTitulos[kv.Key] = kv.Value;
            foreach (KeyValuePair<int, string> kv in dicArtistasTodos)
                if (kv.Key == 0) strArtista = kv.Value; else dicArtistas[kv.Key] = kv.Value;
        }

        /// <summary>Os textos de um tipo de pacote (0x80 títulos, 0x81 intérpretes), por número de faixa (0 = o disco). Devolve os pacotes descartados.</summary>
        private static int DecodificarTipo(byte[] arr, int intTotal, byte bTipo, bool blnDuplo, Encoding enc, Dictionary<int, string> dicSaida, bool blnIgnorarCrc)
        {
            int intDescartados = 0;
            int intLargura = blnDuplo ? 2 : 1;
            List<byte> lstAtual = new List<byte>();
            int intFaixa = -1;
            bool blnValido = false, blnSincronizado = false;
            string strAnterior = null;

            for (int p = 4; p + 18 <= intTotal; p += 18)
            {
                if (arr[p] != bTipo || ((arr[p + 3] >> 4) & 0x07) != 0) continue;
                if (!blnIgnorarCrc && !CrcPacoteOk(arr, p)) { intDescartados++; blnSincronizado = false; continue; }

                if (!blnSincronizado)
                {
                    // Recomeça pelo que o próprio pacote diz: faixa do primeiro
                    // caractere e quantos caracteres desse texto vieram antes.
                    // Se vieram (posição > 0), o começo se perdeu: o texto dessa
                    // faixa não sai.
                    intFaixa = arr[p + 1] & 0x7F;
                    blnValido = (arr[p + 3] & 0x0F) == 0;
                    lstAtual.Clear();
                    blnSincronizado = true;
                }

                for (int c = 0; c + intLargura <= 12; c += intLargura)
                {
                    bool blnFim = arr[p + 4 + c] == 0 && (!blnDuplo || arr[p + 5 + c] == 0);
                    if (!blnFim)
                    {
                        for (int k = 0; k < intLargura; k++) lstAtual.Add(arr[p + 4 + c + k]);
                        continue;
                    }
                    if (blnValido && intFaixa >= 0)
                    {
                        // TAB: "igual à faixa anterior". Conferido antes do Trim, que o apagaria.
                        string strBruto = enc.GetString(lstAtual.ToArray());
                        string strTexto = strBruto.Contains("\t") && strBruto.Trim('\t', ' ').Length == 0
                            ? (strAnterior ?? "") : strBruto.Trim();
                        if (strTexto.Length > 0 && intFaixa > 0) strAnterior = strTexto;
                        if (strTexto.Length > 0) dicSaida[intFaixa] = strTexto;
                    }
                    intFaixa++;
                    lstAtual.Clear();
                    blnValido = true;
                }
            }
            return intDescartados;
        }

        /// <summary>
        /// CRC-16 do pacote (CCITT, polinômio 0x1021, valor inicial 0, resultado
        /// invertido), sobre os 16 primeiros bytes, guardado nos 2 últimos.
        /// Há drives que entregam o campo zerado (não calculam): aí vale o pacote.
        /// </summary>
        public static bool CrcPacoteOk(byte[] arr, int p)
        {
            int intGuardado = (arr[p + 16] << 8) | arr[p + 17];
            if (intGuardado == 0) return true;
            int crc = 0;
            for (int i = 0; i < 16; i++)
            {
                crc ^= arr[p + i] << 8;
                for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return (crc ^ 0xFFFF) == intGuardado;
        }

        public bool LerMcn(out string strMcn)
        {
            strMcn = null;
            if (!IsOpen) return false;

            byte[] arrBuffer = new byte[24];
            GCHandle hPin = GCHandle.Alloc(arrBuffer, GCHandleType.Pinned);
            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arrBuffer.Length, hPin.AddrOfPinnedObject(), 3);
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x42;
                sptd.sptd.Cdb[1] = 0x02;
                sptd.sptd.Cdb[2] = 0x40;
                sptd.sptd.Cdb[3] = 0x02;
                sptd.sptd.Cdb[8] = 24;

                uint dwBytesRetornados;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                bool blnOk = DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero);

                if (blnOk && sptd.sptd.ScsiStatus == 0)
                {
                    if ((arrBuffer[8] & 0x80) != 0)
                    {
                        string strValor = Encoding.ASCII.GetString(arrBuffer, 9, 13).Trim();
                        if (!string.IsNullOrEmpty(strValor) && strValor.Length >= 10)
                        {
                            strMcn = strValor;
                            m_objLogger.RegistrarDiagnostico(string.Format("[SUB-CHANNEL Q] Código de barras MCN/UPC lido com sucesso: {0}", strMcn));
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                hPin.Free();
            }
        }

        public bool LerIsrc(int intFaixa, out string strIsrc)
        {
            strIsrc = null;
            if (!IsOpen) return false;

            byte[] arrBuffer = new byte[24];
            GCHandle hPin = GCHandle.Alloc(arrBuffer, GCHandleType.Pinned);
            try
            {
                SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico(arrBuffer.Length, hPin.AddrOfPinnedObject(), 3);
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x42;
                sptd.sptd.Cdb[1] = 0x02;
                sptd.sptd.Cdb[2] = 0x40;
                sptd.sptd.Cdb[3] = 0x03;
                sptd.sptd.Cdb[6] = (byte)intFaixa;
                sptd.sptd.Cdb[8] = 24;

                uint dwBytesRetornados;
                uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
                bool blnOk = DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero);

                if (blnOk && sptd.sptd.ScsiStatus == 0)
                {
                    if ((arrBuffer[8] & 0x80) != 0)
                    {
                        string strValor = Encoding.ASCII.GetString(arrBuffer, 9, 12).Trim();
                        if (!string.IsNullOrEmpty(strValor) && strValor.Length == 12)
                        {
                            strIsrc = strValor;
                            m_objLogger.RegistrarDiagnostico(string.Format("[SUB-CHANNEL Q] ISRC da Faixa {0:D2} lido com sucesso: {1}", intFaixa, strIsrc));
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                hPin.Free();
            }
        }

        public bool LerBlocoSpti(
            IntPtr ptrDataBuffer,
            uint dwLba,
            uint dwSectors,
            bool blnIsAudio,
            out uint dwBytesTransferidos,
            out byte bScsiStatus,
            out byte bSenseKey,
            out byte bAsc,
            out byte bAscq,
            out int intWin32Error)
        {
            dwBytesTransferidos = 0;
            bScsiStatus = 0;
            bSenseKey = 0;
            bAsc = 0;
            bAscq = 0;
            intWin32Error = 0;

            if (!IsOpen)
            {
                intWin32Error = 6;
                return false;
            }

            uint dwBytesPorSetor = blnIsAudio ? 2352u : 2048u;
            uint dwTamanhoTransferencia = dwSectors * dwBytesPorSetor;

            SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = CriarSptdBasico((int)dwTamanhoTransferencia, ptrDataBuffer, 30);

            if (blnIsAudio)
            {
                sptd.sptd.CdbLength = 12;
                sptd.sptd.Cdb[0] = 0xBE; // READ CD
                sptd.sptd.Cdb[2] = (byte)(dwLba >> 24);
                sptd.sptd.Cdb[3] = (byte)(dwLba >> 16);
                sptd.sptd.Cdb[4] = (byte)(dwLba >> 8);
                sptd.sptd.Cdb[5] = (byte)(dwLba);
                sptd.sptd.Cdb[6] = (byte)(dwSectors >> 16);
                sptd.sptd.Cdb[7] = (byte)(dwSectors >> 8);
                sptd.sptd.Cdb[8] = (byte)(dwSectors);
                sptd.sptd.Cdb[9] = 0x10; // User Data Only (2352 bytes)
            }
            else
            {
                sptd.sptd.CdbLength = 10;
                sptd.sptd.Cdb[0] = 0x28; // READ (10)
                sptd.sptd.Cdb[2] = (byte)(dwLba >> 24);
                sptd.sptd.Cdb[3] = (byte)(dwLba >> 16);
                sptd.sptd.Cdb[4] = (byte)(dwLba >> 8);
                sptd.sptd.Cdb[5] = (byte)(dwLba);
                sptd.sptd.Cdb[7] = (byte)(dwSectors >> 8);
                sptd.sptd.Cdb[8] = (byte)(dwSectors);
            }

            uint dwBytesRetornados;
            uint dwSize = (uint)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER));
            bool blnSucessoIo = DeviceIoControl(m_hDeviceHandle, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, dwSize, ref sptd, dwSize, out dwBytesRetornados, IntPtr.Zero);

            if (!blnSucessoIo)
            {
                intWin32Error = Marshal.GetLastWin32Error();
                return false;
            }

            bScsiStatus = sptd.sptd.ScsiStatus;
            if (bScsiStatus != 0)
            {
                bSenseKey = (byte)(sptd.SenseBuf[2] & 0x0F);
                bAsc = sptd.SenseBuf[12];
                bAscq = sptd.SenseBuf[13];
                return false;
            }

            dwBytesTransferidos = sptd.sptd.DataTransferLength;
            return true;
        }

        public bool DescartarCacheDrive(uint dwLbaAtual, uint dwLbaMaximo)
        {
            if (!IsOpen) return false;

            // READ(10) — o comando que estava aqui — é de disco de dados; em
            // CD de áudio o drive recusa, e o cache nunca era esvaziado: a
            // "nova tentativa" de um setor ruim podia vir da memória do drive.
            // Agora: READ CD de uma região longe, maior que o cache (~3,5 MB).
            const uint dwSetoresPurga = 1500, dwBloco = 26;
            byte[] arrDummy = new byte[dwBloco * 2352];
            GCHandle hPin = GCHandle.Alloc(arrDummy, GCHandleType.Pinned);
            try
            {
                // Longe de onde se está: na outra metade do disco.
                uint dwFlushLba = dwLbaMaximo > dwSetoresPurga * 2 && dwLbaAtual < dwLbaMaximo / 2
                    ? dwLbaMaximo - dwSetoresPurga : 0;
                for (uint d = 0; d < dwSetoresPurga; d += dwBloco)
                {
                    uint b; byte s, k, a, q; int w;
                    LerBlocoSpti(hPin.AddrOfPinnedObject(), dwFlushLba + d, dwBloco, true, out b, out s, out k, out a, out q, out w);
                }
                m_objLogger.RegistrarDiagnostico(string.Format("Cache do drive esvaziado lendo {0} setores a partir do LBA {1}.", dwSetoresPurga, dwFlushLba));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                hPin.Free();
            }
        }

        private static SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER CriarSptdBasico(int intDataTransferLength, IntPtr ptrBuffer, uint dwTimeoutSegundos)
        {
            SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER sptd = new SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER();
            sptd.sptd.Length = (ushort)Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT));
            sptd.sptd.SenseInfoLength = 32;
            sptd.sptd.DataIn = SCSI_IOCTL_DATA_IN;
            sptd.sptd.DataTransferLength = (uint)intDataTransferLength;
            sptd.sptd.TimeOutValue = dwTimeoutSegundos;
            sptd.sptd.DataBuffer = ptrBuffer;
            sptd.sptd.SenseInfoOffset = (uint)(Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT)) + sizeof(uint));
            sptd.sptd.Cdb = new byte[16];
            sptd.SenseBuf = new byte[32];
            return sptd;
        }

        public void Dispose()
        {
            FecharUnidade();
        }
    }
}
