using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ARCA.Interop
{
    /// <summary>
    /// Codifica PCM de CD (44,1 kHz, 16 bits, estéreo) em WMA 9.2 Lossless
    /// pelo IWMWriter do wmvcore.dll — o mesmo Windows Media Format que o
    /// ARCA já usa para ler (LeitorAudio) e para as tags (EditorAsf).
    ///
    /// Substitui o Microsoft Expression Encoder 4 (04/10/2026). O Encoder não
    /// tinha codificador próprio: os WMA do acervo declaram WMFSDKVersion
    /// 12.0.7601.17514, que é o wmvcore.dll da Windows 7. O codec é o mesmo; sai
    /// a camada do meio, e com ela o WAV temporário — o PCM vem direto do .bin.
    ///
    /// O que garante que a troca não regride não é este código: é a
    /// conferência bit a bit que a AQUISIÇÃO faz em toda faixa (o WMA
    /// decodificado tem de ter o mesmo SHA-256 do PCM lido do disco).
    ///
    /// Interfaces e ordem das vtables tiradas do wmsdkidl.h do SDK 7.0A.
    /// Uso: Abrir → Escrever (quantas vezes for) → Concluir.
    /// </summary>
    public sealed class EscritorWma : IDisposable
    {
        // ---- funções de entrada do wmvcore.dll ------------------------
        [DllImport("wmvcore.dll", ExactSpelling = true)]
        private static extern int WMCreateWriter(IntPtr pUnkCert, out IWMWriter ppWriter);

        [DllImport("wmvcore.dll", ExactSpelling = true)]
        private static extern int WMCreateProfileManager(out IWMProfileManager ppProfileManager);

        // ---- interfaces (wmsdkidl.h) ----------------------------------
        [ComImport, Guid("96406BD4-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMWriter
        {
            void SetProfileByID(ref Guid guidProfile);
            void SetProfile(IWMProfile pProfile);
            void SetOutputFilename([MarshalAs(UnmanagedType.LPWStr)] string pwszFilename);
            void GetInputCount(out uint pcInputs);
            void GetInputProps(uint dwInputNum, out IWMInputMediaProps ppInput);
            void SetInputProps(uint dwInputNum, IWMInputMediaProps pInput);
            void GetInputFormatCount(uint dwInputNumber, out uint pcFormats);
            void GetInputFormat(uint dwInputNumber, uint dwFormatNumber, out IWMInputMediaProps pProps);
            void BeginWriting();
            void EndWriting();
            void AllocateSample(uint dwSampleSize, out INSSBuffer ppSample);
            void WriteSample(uint dwInputNum, ulong cnsSampleTime, uint dwFlags, INSSBuffer pSample);
            void Flush();
        }

        [ComImport, Guid("d16679f2-6ca0-472d-8d31-2f5d55aee155"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMProfileManager
        {
            void CreateEmptyProfile(uint dwVersion, out IWMProfile ppProfile);
            void LoadProfileByID(ref Guid guidProfile, out IWMProfile ppProfile);
            void LoadProfileByData([MarshalAs(UnmanagedType.LPWStr)] string pwszProfile, out IWMProfile ppProfile);
            void SaveProfile(IWMProfile pIWMProfile, IntPtr pwszProfile, ref uint pdwLength);
            void GetSystemProfileCount(out uint pcProfiles);
            void LoadSystemProfile(uint dwProfileIndex, out IWMProfile ppProfile);
        }

        [ComImport, Guid("96406BDB-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMProfile
        {
            void GetVersion(out uint pdwVersion);
            void GetName(IntPtr pwszName, ref uint pcchName);
            void SetName([MarshalAs(UnmanagedType.LPWStr)] string pwszName);
            void GetDescription(IntPtr pwszDescription, ref uint pcchDescription);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pwszDescription);
            void GetStreamCount(out uint pcStreams);
            void GetStream(uint dwStreamIndex, out IWMStreamConfig ppConfig);
            void GetStreamByNumber(ushort wStreamNum, out IWMStreamConfig ppConfig);
            void RemoveStream(IWMStreamConfig pConfig);
            void RemoveStreamByNumber(ushort wStreamNum);
            void AddStream(IWMStreamConfig pConfig);
            void ReconfigStream(IWMStreamConfig pConfig);
            void CreateNewStream(ref Guid guidStreamType, out IWMStreamConfig ppConfig);
            // (exclusão mútua — não usada)
        }

        [ComImport, Guid("96406BDC-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMStreamConfig
        {
            void GetStreamType(out Guid pguidStreamType);
            void GetStreamNumber(out ushort pwStreamNum);
            void SetStreamNumber(ushort wStreamNum);
            void GetStreamName(IntPtr pwszStreamName, ref ushort pcchStreamName);
            void SetStreamName([MarshalAs(UnmanagedType.LPWStr)] string pwszStreamName);
            void GetConnectionName(IntPtr pwszInputName, ref ushort pcchInputName);
            void SetConnectionName([MarshalAs(UnmanagedType.LPWStr)] string pwszInputName);
            void GetBitrate(out uint pdwBitrate);
            void SetBitrate(uint pdwBitrate);
            void GetBufferWindow(out uint pmsBufferWindow);
            void SetBufferWindow(uint msBufferWindow);
        }

        [ComImport, Guid("96406BCE-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMMediaProps
        {
            void GetType(out Guid pguidType);
            [PreserveSig] int GetMediaType(IntPtr pType, ref uint pcbType);
            void SetMediaType(IntPtr pType);
        }

        [ComImport, Guid("96406BD5-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMInputMediaProps
        {
            // IWMMediaProps
            void GetType(out Guid pguidType);
            [PreserveSig] int GetMediaType(IntPtr pType, ref uint pcbType);
            void SetMediaType(IntPtr pType);
            // IWMInputMediaProps
            void GetConnectionName(IntPtr pwszName, ref ushort pcchName);
            void GetGroupName(IntPtr pwszName, ref ushort pcchName);
        }

        [ComImport, Guid("7e51f487-4d93-4f98-8ab4-27d0565adc51"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMCodecInfo3
        {
            // IWMCodecInfo
            void GetCodecInfoCount(ref Guid guidType, out uint pcCodecs);
            void GetCodecFormatCount(ref Guid guidType, uint dwCodecIndex, out uint pcFormat);
            void GetCodecFormat(ref Guid guidType, uint dwCodecIndex, uint dwFormatIndex, out IWMStreamConfig ppIStreamConfig);
            // IWMCodecInfo2
            void GetCodecName(ref Guid guidType, uint dwCodecIndex, IntPtr wszName, ref uint pcchName);
            void GetCodecFormatDesc(ref Guid guidType, uint dwCodecIndex, uint dwFormatIndex, out IWMStreamConfig ppIStreamConfig,
                                    IntPtr wszDesc, ref uint pcchDesc);
            // IWMCodecInfo3
            void GetCodecFormatProp(ref Guid guidType, uint dwCodecIndex, uint dwFormatIndex, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                    out int pType, IntPtr pValue, ref uint pdwSize);
            void GetCodecProp(ref Guid guidType, uint dwCodecIndex, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                              out int pType, IntPtr pValue, ref uint pdwSize);
            void SetCodecEnumerationSetting(ref Guid guidType, uint dwCodecIndex, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                            int Type, IntPtr pValue, uint dwSize);
            void GetCodecEnumerationSetting(ref Guid guidType, uint dwCodecIndex, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                            out int pType, IntPtr pValue, ref uint pdwSize);
        }

        [ComImport, Guid("72995A79-5090-42a4-9C8C-D9D0B6D34BE5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMPropertyVault
        {
            void GetPropertyCount(out uint pdwCount);
            void GetPropertyByName([MarshalAs(UnmanagedType.LPWStr)] string pszName, out int pType, IntPtr pValue, ref uint pdwSize);
            void SetProperty([MarshalAs(UnmanagedType.LPWStr)] string pszName, int pType, IntPtr pValue, uint dwSize);
            void GetPropertyByIndex(uint dwIndex, IntPtr pszName, ref uint pdwNameLen, out int pType, IntPtr pValue, ref uint pdwSize);
            void CopyPropertiesFrom(IWMPropertyVault pIWMPropertyVault);
            void Clear();
        }

        // Só o começo de IWMHeaderInfo: SetAttribute é o 4º método.
        [ComImport, Guid("96406BDA-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMHeaderInfo
        {
            void GetAttributeCount(ushort wStreamNum, out ushort pcAttributes);
            void GetAttributeByIndex(ushort wIndex, ref ushort pwStreamNum, IntPtr pwszName, ref ushort pcchNameLen,
                                     out int pType, IntPtr pValue, ref ushort pcbLength);
            [PreserveSig]
            int GetAttributeByName(ref ushort pwStreamNum, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                                   out int pType, IntPtr pValue, ref ushort pcbLength);
            void SetAttribute(ushort wStreamNum, [MarshalAs(UnmanagedType.LPWStr)] string pszName, int Type, IntPtr pValue, ushort cbLength);
        }

        [ComImport, Guid("E1CD3524-03D7-11d2-9EED-006097D2D7CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INSSBuffer
        {
            void GetLength(out uint pdwLength);
            void SetLength(uint dwLength);
            void GetMaxLength(out uint pdwLength);
            void GetBuffer(out IntPtr ppdwBuffer);
            void GetBufferAndLength(out IntPtr ppdwBuffer, out uint pdwLength);
        }

        // ---- constantes (wmsdkidl.h) ----------------------------------
        private static readonly Guid WMMEDIATYPE_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid WMMEDIASUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid WMMEDIASUBTYPE_WMAudio_Lossless = new Guid("00000163-0000-0010-8000-00AA00389B71");
        private static readonly Guid WMFORMAT_WaveFormatEx = new Guid("05589f81-c356-11ce-bf01-00aa0055595a");
        private const uint WMT_VER_9_0 = 0x90000;
        private const int WMT_TYPE_DWORD = 0, WMT_TYPE_STRING = 1, WMT_TYPE_BOOL = 3;
        private const string g_wszVBREnabled = "_VBRENABLED";
        private const string g_wszVBRQuality = "_VBRQUALITY";
        private const string g_wszNumPasses = "_PASSESUSED";

        // WM_MEDIA_TYPE em x86 (72 bytes): major 0, sub 16, bFixedSizeSamples 32,
        // bTemporalCompression 36, lSampleSize 40, formattype 44, pUnk 60,
        // cbFormat 64, pbFormat 68. O ARCA é x86 de propósito.
        private const int TamanhoMediaType = 72;

        public const int Taxa = 44100, Canais = 2, Bits = 16;
        private const int BytesPorQuadro = Canais * Bits / 8;

        // ---- estado -----------------------------------------------------
        private IWMWriter _escritor;
        private bool _blnEscrevendo;
        private long _lngQuadros;

        public string Falha { get; private set; }

        /// <summary>A descrição que o codec dá ao formato escolhido (vai para o registro).</summary>
        public string Formato { get; private set; }

        /// <summary>
        /// Prepara o arquivo: perfil WMA Lossless 44,1/16/2 (VBR qualidade
        /// 100, uma passada, como o Encoder fazia), entrada PCM no mesmo
        /// formato — sem conversão nenhuma no caminho — e as tags que o Encoder
        /// gravava. Textos vazios são omitidos; int vira DWORD (WM/TrackNumber
        /// era DWORD nos arquivos do Encoder, e continua).
        /// </summary>
        public bool Abrir(string strArquivo, IList<KeyValuePair<string, object>> lstAtributos)
        {
            Fechar();
            Falha = null;
            _lngQuadros = 0;
            IWMProfileManager objGerente = null;
            IWMProfile objPerfil = null;
            IWMStreamConfig objFluxo = null;
            IWMInputMediaProps objEntrada = null;
            try
            {
                int hr = WMCreateProfileManager(out objGerente);
                if (hr < 0 || objGerente == null) { Falha = "WMCreateProfileManager: 0x" + hr.ToString("X8"); return false; }

                string strFormato;
                objFluxo = FormatoLossless((IWMCodecInfo3)objGerente, out strFormato);
                if (objFluxo == null) { Falha = "o Windows não ofereceu o formato WMA Lossless 44,1 kHz, 16 bits, estéreo"; return false; }
                Formato = strFormato;

                // Qualidade 100 é o que torna o VBR do WMA Lossless sem perdas.
                IWMPropertyVault objCofre = (IWMPropertyVault)objFluxo;
                DefinirPropriedade(objCofre, g_wszVBREnabled, WMT_TYPE_BOOL, 1);
                DefinirPropriedade(objCofre, g_wszVBRQuality, WMT_TYPE_DWORD, 100);

                objFluxo.SetStreamNumber(1);
                objFluxo.SetStreamName("Audio Stream");
                objFluxo.SetConnectionName("Audio");

                objGerente.CreateEmptyProfile(WMT_VER_9_0, out objPerfil);
                objPerfil.AddStream(objFluxo);

                hr = WMCreateWriter(IntPtr.Zero, out _escritor);
                if (hr < 0 || _escritor == null) { Falha = "WMCreateWriter: 0x" + hr.ToString("X8"); return false; }
                _escritor.SetProfile(objPerfil);
                _escritor.SetOutputFilename(strArquivo);

                uint intEntradas;
                _escritor.GetInputCount(out intEntradas);
                if (intEntradas != 1) { Falha = "o perfil tem " + intEntradas + " entradas; esperava 1"; return false; }
                _escritor.GetInputProps(0, out objEntrada);
                DefinirEntradaPcm(objEntrada);
                _escritor.SetInputProps(0, objEntrada);

                if (lstAtributos != null)
                {
                    IWMHeaderInfo objCabecalho = (IWMHeaderInfo)_escritor;
                    foreach (KeyValuePair<string, object> kv in lstAtributos) DefinirAtributo(objCabecalho, kv.Key, kv.Value);
                }

                _escritor.BeginWriting();
                _blnEscrevendo = true;
                return true;
            }
            catch (Exception ex)
            {
                Falha = Descrever(ex);
                Fechar();
                return false;
            }
            finally
            {
                Soltar(objEntrada);
                Soltar(objFluxo);
                Soltar(objPerfil);
                Soltar(objGerente);
            }
        }

        /// <summary>
        /// Acrescenta PCM (intercalado, little-endian, quadros inteiros). O
        /// tempo de cada amostra sai da contagem exata de quadros, não de soma
        /// de durações, para não acumular arredondamento.
        /// </summary>
        public bool Escrever(byte[] arrPcm, int intInicio, int intBytes)
        {
            if (!_blnEscrevendo) { if (Falha == null) Falha = "o escritor não está aberto"; return false; }
            if (intBytes <= 0) return true;
            if (intBytes % BytesPorQuadro != 0) { Falha = "bloco de PCM com quadro incompleto (" + intBytes + " bytes)"; return false; }
            INSSBuffer objAmostra = null;
            try
            {
                _escritor.AllocateSample((uint)intBytes, out objAmostra);
                IntPtr p;
                objAmostra.GetBuffer(out p);
                Marshal.Copy(arrPcm, intInicio, p, intBytes);
                objAmostra.SetLength((uint)intBytes);
                ulong cnsTempo = (ulong)(_lngQuadros * 10000000L / Taxa);
                _escritor.WriteSample(0, cnsTempo, 0, objAmostra);
                _lngQuadros += intBytes / BytesPorQuadro;
                return true;
            }
            catch (Exception ex) { Falha = Descrever(ex); return false; }
            finally { Soltar(objAmostra); }
        }

        /// <summary>Fecha o arquivo (EndWriting grava o cabeçalho final e o índice). false se algo falhou no caminho.</summary>
        public bool Concluir()
        {
            if (!_blnEscrevendo) return false;
            try
            {
                _escritor.EndWriting();
                _blnEscrevendo = false;
                return Falha == null;
            }
            catch (Exception ex) { Falha = Descrever(ex); return false; }
            finally { Fechar(); }
        }

        /// <summary>Quadros escritos até agora.</summary>
        public long Quadros { get { return _lngQuadros; } }

        // ===============================================================
        /// <summary>
        /// Procura, entre os codecs de áudio do Windows, o WMA Lossless e nele o
        /// formato 44,1 kHz / 16 bits / 2 canais, com a enumeração em VBR de
        /// uma passada (é como o Lossless aparece). null se não houver.
        /// </summary>
        private static IWMStreamConfig FormatoLossless(IWMCodecInfo3 objCodecs, out string strDescricao)
        {
            strDescricao = null;
            Guid guidAudio = WMMEDIATYPE_Audio;
            uint intCodecs;
            objCodecs.GetCodecInfoCount(ref guidAudio, out intCodecs);
            for (uint c = 0; c < intCodecs; c++)
            {
                try
                {
                    EnumeracaoVbr(objCodecs, c);
                    uint intFormatos;
                    objCodecs.GetCodecFormatCount(ref guidAudio, c, out intFormatos);
                    for (uint f = 0; f < intFormatos; f++)
                    {
                        IWMStreamConfig objFluxo;
                        objCodecs.GetCodecFormat(ref guidAudio, c, f, out objFluxo);
                        if (objFluxo == null) continue;
                        if (EhLossless441(objFluxo))
                        {
                            strDescricao = DescricaoDoFormato(objCodecs, c, f);
                            return objFluxo;
                        }
                        Soltar(objFluxo);
                    }
                }
                catch (COMException) { }   // codec que não aceita a enumeração em VBR: não é o Lossless
            }
            return null;
        }

        private static void EnumeracaoVbr(IWMCodecInfo3 objCodecs, uint intCodec)
        {
            Guid guidAudio = WMMEDIATYPE_Audio;
            IntPtr p = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(p, 1);   // TRUE
                objCodecs.SetCodecEnumerationSetting(ref guidAudio, intCodec, g_wszVBREnabled, WMT_TYPE_BOOL, p, 4);
                Marshal.WriteInt32(p, 1);   // uma passada
                objCodecs.SetCodecEnumerationSetting(ref guidAudio, intCodec, g_wszNumPasses, WMT_TYPE_DWORD, p, 4);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static bool EhLossless441(IWMStreamConfig objFluxo)
        {
            IWMMediaProps objProps = (IWMMediaProps)objFluxo;
            uint intTamanho = 0;
            objProps.GetMediaType(IntPtr.Zero, ref intTamanho);
            if (intTamanho < TamanhoMediaType) return false;
            IntPtr p = Marshal.AllocHGlobal((int)intTamanho);
            try
            {
                if (objProps.GetMediaType(p, ref intTamanho) < 0) return false;
                if (LerGuid(p, 16) != WMMEDIASUBTYPE_WMAudio_Lossless) return false;
                if (LerGuid(p, 44) != WMFORMAT_WaveFormatEx) return false;
                IntPtr pFormato = Marshal.ReadIntPtr(p, 68);
                if (pFormato == IntPtr.Zero || Marshal.ReadInt32(p, 64) < 16) return false;
                // WAVEFORMATEX: wFormatTag 0, nChannels 2, nSamplesPerSec 4, ..., wBitsPerSample 14
                return Marshal.ReadInt16(pFormato, 2) == Canais
                    && Marshal.ReadInt32(pFormato, 4) == Taxa
                    && Marshal.ReadInt16(pFormato, 14) == Bits;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static string DescricaoDoFormato(IWMCodecInfo3 objCodecs, uint intCodec, uint intFormato)
        {
            try
            {
                Guid guidAudio = WMMEDIATYPE_Audio;
                IWMStreamConfig objTemp;
                uint intLen = 0;
                objCodecs.GetCodecFormatDesc(ref guidAudio, intCodec, intFormato, out objTemp, IntPtr.Zero, ref intLen);
                Soltar(objTemp);
                if (intLen == 0) return null;
                IntPtr p = Marshal.AllocHGlobal((int)intLen * 2);
                try
                {
                    objCodecs.GetCodecFormatDesc(ref guidAudio, intCodec, intFormato, out objTemp, p, ref intLen);
                    Soltar(objTemp);
                    return Marshal.PtrToStringUni(p);
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch { return null; }
        }

        /// <summary>A entrada do escritor: PCM 44,1/16/2, exatamente o formato do CD.</summary>
        private static void DefinirEntradaPcm(IWMInputMediaProps objEntrada)
        {
            IntPtr pTipo = Marshal.AllocHGlobal(TamanhoMediaType);
            IntPtr pFormato = Marshal.AllocHGlobal(18);
            try
            {
                for (int i = 0; i < TamanhoMediaType; i++) Marshal.WriteByte(pTipo, i, 0);
                EscreverGuid(pTipo, 0, WMMEDIATYPE_Audio);
                EscreverGuid(pTipo, 16, WMMEDIASUBTYPE_PCM);
                Marshal.WriteInt32(pTipo, 32, 1);                  // bFixedSizeSamples
                Marshal.WriteInt32(pTipo, 36, 0);                  // bTemporalCompression
                Marshal.WriteInt32(pTipo, 40, BytesPorQuadro);     // lSampleSize
                EscreverGuid(pTipo, 44, WMFORMAT_WaveFormatEx);
                Marshal.WriteInt32(pTipo, 64, 18);                 // cbFormat
                Marshal.WriteIntPtr(pTipo, 68, pFormato);

                Marshal.WriteInt16(pFormato, 0, 1);                       // WAVE_FORMAT_PCM
                Marshal.WriteInt16(pFormato, 2, Canais);
                Marshal.WriteInt32(pFormato, 4, Taxa);
                Marshal.WriteInt32(pFormato, 8, Taxa * BytesPorQuadro);   // nAvgBytesPerSec
                Marshal.WriteInt16(pFormato, 12, BytesPorQuadro);         // nBlockAlign
                Marshal.WriteInt16(pFormato, 14, Bits);
                Marshal.WriteInt16(pFormato, 16, 0);                      // cbSize

                objEntrada.SetMediaType(pTipo);
            }
            finally
            {
                Marshal.FreeHGlobal(pFormato);
                Marshal.FreeHGlobal(pTipo);
            }
        }

        private static void DefinirPropriedade(IWMPropertyVault objCofre, string strNome, int intTipo, int intValor)
        {
            IntPtr p = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(p, intValor);
                objCofre.SetProperty(strNome, intTipo, p, 4);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static void DefinirAtributo(IWMHeaderInfo objCabecalho, string strNome, object objValor)
        {
            if (objValor == null) return;
            if (objValor is int)
            {
                IntPtr p = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(p, (int)objValor);
                    objCabecalho.SetAttribute(0, strNome, WMT_TYPE_DWORD, p, 4);
                }
                finally { Marshal.FreeHGlobal(p); }
                return;
            }
            string strTexto = objValor.ToString();
            if (strTexto.Length == 0) return;
            IntPtr pTexto = Marshal.StringToHGlobalUni(strTexto);
            try { objCabecalho.SetAttribute(0, strNome, WMT_TYPE_STRING, pTexto, (ushort)((strTexto.Length + 1) * 2)); }
            finally { Marshal.FreeHGlobal(pTexto); }
        }

        private static Guid LerGuid(IntPtr p, int intOffset)
        {
            byte[] arr = new byte[16];
            Marshal.Copy(new IntPtr(p.ToInt64() + intOffset), arr, 0, 16);
            return new Guid(arr);
        }

        private static void EscreverGuid(IntPtr p, int intOffset, Guid g)
        {
            Marshal.Copy(g.ToByteArray(), 0, new IntPtr(p.ToInt64() + intOffset), 16);
        }

        private static string Descrever(Exception ex)
        {
            COMException objCom = ex as COMException;
            return objCom != null
                ? string.Format("{0} (0x{1:X8})", objCom.Message.Trim(), objCom.ErrorCode)
                : ex.GetType().Name + ": " + ex.Message;
        }

        private static void Soltar(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        public void Fechar()
        {
            if (_escritor != null)
            {
                // Abandonado no meio (erro ou cancelamento): EndWriting fecha o
                // arquivo; quem chamou é que decide apagá-lo.
                if (_blnEscrevendo) { try { _escritor.EndWriting(); } catch { } }
                Soltar(_escritor);
            }
            _escritor = null;
            _blnEscrevendo = false;
        }

        public void Dispose() { Fechar(); }
    }
}
