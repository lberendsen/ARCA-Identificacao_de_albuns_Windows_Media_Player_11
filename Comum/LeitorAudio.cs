using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ARCA.Interop
{
    /// <summary>
    /// Decodifica áudio para PCM pelo `IWMSyncReader` do wmvcore.dll.
    ///
    /// É daqui que saem as amostras dos medidores e da forma de onda. A
    /// alternativa era um plugin DSP, que exigiria um servidor COM registrado
    /// na máquina — e a sonda mostrou que não há nenhum DSP registrado ali, nem
    /// a categoria no registro, então nem se sabia se o WMP carregaria o nosso.
    ///
    /// Há também um argumento de mérito: isto mede **o que está na gravação**,
    /// não o que a cadeia de saída do player fez com ela.
    ///
    /// IIDs e ordem de vtable conferidos no wmsdkidl.h e wmsbuffer.h do SDK.
    /// Medido: 64x o tempo real numa máquina moderna, e basta 1x.
    /// </summary>
    public sealed class LeitorAudio : IDisposable
    {
        [DllImport("wmvcore.dll", ExactSpelling = true)]
        private static extern int WMCreateSyncReader(IntPtr cert, uint direitos,
                                                     out IWMSyncReader leitor);

        [ComImport, Guid("9397F121-7705-4dc9-B049-98B698188414"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMSyncReader
        {
            void Open([MarshalAs(UnmanagedType.LPWStr)] string arquivo);
            void Close();
            void SetRange(ulong inicio, long duracao);
            void SetRangeByFrame(ushort stream, ulong quadro, long quadros);
            [PreserveSig]
            int GetNextSample(ushort stream, out INSSBuffer amostra, out ulong tempo,
                              out ulong duracao, out uint flags, out uint saida,
                              out ushort streamSaida);
            // LPArray explícito: em interface COM, array sem anotação vai como
            // SAFEARRAY, e o wmvcore espera ponteiro simples (WORD*, enum*).
            void SetStreamsSelected(ushort qtd,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ushort[] streams,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] selecoes);
            void GetStreamSelected(ushort stream, out int selecao);
            void SetReadStreamSamples(ushort stream, [MarshalAs(UnmanagedType.Bool)] bool comprimido);
            void GetReadStreamSamples(ushort stream, [MarshalAs(UnmanagedType.Bool)] out bool comprimido);
            void GetOutputSetting(uint saida, [MarshalAs(UnmanagedType.LPWStr)] string nome,
                                  out int tipo, IntPtr valor, ref ushort tamanho);
            void SetOutputSetting(uint saida, [MarshalAs(UnmanagedType.LPWStr)] string nome,
                                  int tipo, IntPtr valor, ushort tamanho);
            void GetOutputCount(out uint qtd);
            void GetOutputProps(uint saida, out IWMMediaProps props);
            void SetOutputProps(uint saida, IWMMediaProps props);
            void GetOutputFormatCount(uint saida, out uint qtd);
            void GetOutputFormat(uint saida, uint formato, out IWMMediaProps props);
            void GetOutputNumberForStream(ushort stream, out uint saida);
            void GetStreamNumberForOutput(uint saida, out ushort stream);
            void GetMaxOutputSampleSize(uint saida, out uint tamanho);
            void GetMaxStreamSampleSize(ushort stream, out uint tamanho);
            void OpenStream(IntPtr fluxo);
        }

        // Só o começo da vtable de IWMHeaderInfo (wmsdkidl.h): o leitor a
        // implementa, e dela só se quer a duração declarada no cabeçalho.
        [ComImport, Guid("96406BDA-2B2B-11d3-B36B-00C04F6108FF"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMHeaderInfo
        {
            void GetAttributeCount(ushort stream, out ushort qtd);
            void GetAttributeByIndex(ushort indice, ref ushort stream, IntPtr nome, ref ushort nomeLen,
                                     out int tipo, IntPtr valor, ref ushort valorLen);
            [PreserveSig]
            int GetAttributeByName(ref ushort stream, [MarshalAs(UnmanagedType.LPWStr)] string nome,
                                   out int tipo, IntPtr valor, ref ushort valorLen);
        }

        [ComImport, Guid("E1CD3524-03D7-11d2-9EED-006097D2D7CF"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INSSBuffer
        {
            void GetLength(out uint tamanho);
            void SetLength(uint tamanho);
            void GetMaxLength(out uint tamanho);
            void GetBuffer(out IntPtr buffer);
            void GetBufferAndLength(out IntPtr buffer, out uint tamanho);
        }

        [ComImport, Guid("96406BCE-2B2B-11d3-B36B-00C04F6108FF"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMMediaProps
        {
            void GetType(out Guid tipo);
            [PreserveSig] int GetMediaType(IntPtr tipo, ref uint tamanho);
            void SetMediaType(IntPtr tipo);
        }

        private const int NS_E_NO_MORE_SAMPLES = unchecked((int)0xC00D0BCF);

        // WM_MEDIA_TYPE em x86: 2 GUIDs (32) + 3 campos de 4 (12) + GUID (16)
        // + pUnk (4) + cbFormat (4) + pbFormat (4) = 72 bytes.
        private const int OffsetCbFormat = 64;
        private const int OffsetPbFormat = 68;

        private IWMSyncReader _leitor;

        // A saída de áudio escolhida. GetNextSample(0) entrega amostras de
        // TODOS os fluxos: num WMV, vídeo seria lido como se fosse áudio.
        private int _intSaidaAudio = -1;

        public int Canais { get; private set; }
        public int Taxa { get; private set; }
        public int Bits { get; private set; }
        public string Falha { get; private set; }
        public bool Aberto { get { return _leitor != null && Canais > 0; } }

        /// <summary>
        /// true só quando o arquivo acabou de verdade (NS_E_NO_MORE_SAMPLES).
        /// Os ProximoBloco devolvem null tanto no fim quanto em erro; quem
        /// guarda o resultado (onda, espectrograma) tem de conferir isto, ou
        /// um erro no meio da faixa vira um cache truncado que parece bom.
        /// </summary>
        public bool Terminou { get; private set; }

        /// <summary>
        /// Duração que o cabeçalho ASF declara, em segundos; 0 se não diz.
        /// Arquivo cortado no fim termina "normalmente" para o decodificador
        /// — só comparando com isto se percebe que falta um pedaço.
        /// </summary>
        public double DuracaoDeclarada { get; private set; }

        private double LerDuracaoDeclarada()
        {
            IWMHeaderInfo objCabecalho = _leitor as IWMHeaderInfo;
            if (objCabecalho == null) return 0;
            IntPtr pValor = Marshal.AllocCoTaskMem(8);
            try
            {
                ushort stream = 0, tamanho = 8;
                int tipo;
                int hr = objCabecalho.GetAttributeByName(ref stream, "Duration", out tipo, pValor, ref tamanho);
                if (hr < 0 || tamanho != 8) return 0;
                return (ulong)Marshal.ReadInt64(pValor) / 10000000.0;
            }
            catch { return 0; }
            finally { Marshal.FreeCoTaskMem(pValor); }
        }

        /// <summary>
        /// Segundos que faltam entre o que se decodificou e o que o cabeçalho
        /// declara, ou 0 se não falta nada que importe (ou se não se sabe).
        /// </summary>
        public double FaltaNoFim(long lngQuadrosDecodificados)
        {
            if (DuracaoDeclarada <= 0 || Taxa <= 0) return 0;
            double dblFalta = DuracaoDeclarada - (double)lngQuadrosDecodificados / Taxa;
            return dblFalta > ToleranciaFimSegundos ? dblFalta : 0;
        }

        /// <summary>Folga entre a duração declarada e a decodificada que ainda conta como inteira.</summary>
        public const double ToleranciaFimSegundos = 1.0;

        /// <summary>Tempo, em segundos, do último bloco entregue.</summary>
        public double TempoDoUltimoBloco { get; private set; }

        public bool Abrir(string arquivo)
        {
            Fechar();
            Falha = null;
            Terminou = false;
            try
            {
                int hr = WMCreateSyncReader(IntPtr.Zero, 0, out _leitor);
                if (hr < 0 || _leitor == null)
                {
                    Falha = "WMCreateSyncReader: 0x" + hr.ToString("X8");
                    return false;
                }
                _leitor.Open(arquivo);

                uint saidas;
                _leitor.GetOutputCount(out saidas);
                for (uint s = 0; s < saidas; s++)
                {
                    PedirSaidaOriginal(s);
                    if (LerFormato(s)) { _intSaidaAudio = (int)s; break; }
                }

                if (Canais <= 0) { Falha = "Nenhuma saída de áudio PCM."; Fechar(); return false; }
                DesligarOutrosFluxos(saidas);
                DuracaoDeclarada = LerDuracaoDeclarada();
                return true;
            }
            catch (Exception ex)
            {
                Falha = ex.GetType().Name + ": " + ex.Message;
                Fechar();
                return false;
            }
        }

        /// <summary>
        /// Desliga, no leitor, todo fluxo que não é a saída de áudio escolhida.
        ///
        /// Sem isto, num WMV o IWMSyncReader decodificava cada quadro de vídeo
        /// — por software, sem a placa de vídeo — só para o LerAmostra jogá-lo
        /// fora. Ao tocar um vídeo, o ARCA abre até três leitores do mesmo
        /// arquivo (instrumentos, forma de onda, espectrograma), dois deles em
        /// velocidade máxima: a CPU ia a 100% (Lorenzo, 04/10/2026). O áudio
        /// entregue não muda — só deixa de existir o trabalho descartado.
        ///
        /// Falha aqui não impede a leitura: o LerAmostra continua filtrando
        /// por saída, como antes.
        /// </summary>
        private void DesligarOutrosFluxos(uint intSaidas)
        {
            if (intSaidas <= 1 || _intSaidaAudio < 0) return;
            try
            {
                List<ushort> lstFluxos = new List<ushort>();
                for (uint s = 0; s < intSaidas; s++)
                {
                    if (s == (uint)_intSaidaAudio) continue;
                    ushort intFluxo;
                    _leitor.GetStreamNumberForOutput(s, out intFluxo);
                    lstFluxos.Add(intFluxo);
                }
                if (lstFluxos.Count == 0) return;
                int[] arrSelecao = new int[lstFluxos.Count];   // WMT_OFF = 0
                _leitor.SetStreamsSelected((ushort)lstFluxos.Count, lstFluxos.ToArray(), arrSelecao);
                FluxosDesligados = lstFluxos.Count;
            }
            catch { }
        }

        /// <summary>Quantos fluxos que não eram o áudio foram desligados (vídeo, roteiro…). 0 num arquivo só de áudio.</summary>
        public int FluxosDesligados { get; private set; }

        /// <summary>Reposiciona a leitura. Usado quando a reprodução salta.</summary>
        public bool Posicionar(double segundos)
        {
            if (_leitor == null) return false;
            try
            {
                if (segundos < 0) segundos = 0;
                _leitor.SetRange((ulong)(segundos * 10000000.0), 0);
                TempoDoUltimoBloco = segundos;
                return true;
            }
            catch (Exception ex) { Falha = ex.Message; return false; }
        }

        /// <summary>
        /// A próxima amostra que traz pelo menos um quadro. false no fim do
        /// arquivo (<see cref="Terminou"/>) ou em erro (<see cref="Falha"/>).
        /// Amostra vazia não é fim: é pulada. Quem chama libera buf.
        /// </summary>
        private bool LerAmostra(out INSSBuffer buf, out IntPtr p, out int bytes)
        {
            int passo = (Bits / 8) * Canais;
            while (true)
            {
                buf = null;
                p = IntPtr.Zero;
                bytes = 0;
                ulong tempo, duracao;
                uint flags, saida;
                ushort stream;
                int hr = _leitor.GetNextSample(0, out buf, out tempo, out duracao,
                                               out flags, out saida, out stream);
                if (hr == NS_E_NO_MORE_SAMPLES) { Terminou = true; return false; }
                if (hr < 0)
                {
                    Falha = string.Format("erro de decodificação perto de {0:F1} s (0x{1:X8})", TempoDoUltimoBloco, hr);
                    return false;
                }
                if (passo <= 0) { Falha = "formato de áudio sem tamanho de amostra"; return false; }
                if (buf == null) continue;
                if (_intSaidaAudio >= 0 && saida != (uint)_intSaidaAudio)
                {
                    // Amostra de outro fluxo (vídeo, roteiro): não é áudio.
                    Marshal.ReleaseComObject(buf);
                    buf = null;
                    continue;
                }

                TempoDoUltimoBloco = tempo / 10000000.0;
                uint uintBytes;
                buf.GetBufferAndLength(out p, out uintBytes);
                bytes = (int)uintBytes;
                if (bytes >= passo) return true;

                Marshal.ReleaseComObject(buf);
                buf = null;
            }
        }

        /// <summary>
        /// Próximo bloco exatamente como o decodificador entrega: PCM
        /// intercalado, little-endian, sem conversão nenhuma. É o que as
        /// conferências bit a bit comparam com o CD. null no fim ou em erro
        /// (ver Terminou).
        /// </summary>
        public byte[] ProximoBlocoBytes()
        {
            if (_leitor == null) return null;
            INSSBuffer buf = null;
            try
            {
                IntPtr p;
                int bytes;
                if (!LerAmostra(out buf, out p, out bytes)) return null;
                byte[] arr = new byte[bytes];
                Marshal.Copy(p, arr, 0, bytes);
                return arr;
            }
            catch (Exception ex) { Falha = ex.Message; return null; }
            finally
            {
                if (buf != null) { try { Marshal.ReleaseComObject(buf); } catch { } }
            }
        }

        /// <summary>
        /// Próximo bloco decodificado, já separado por canal e normalizado
        /// em -1..1. Devolve null no fim do arquivo ou em erro (ver Terminou).
        /// </summary>
        public float[][] ProximoBloco()
        {
            if (_leitor == null) return null;

            INSSBuffer buf = null;
            try
            {
                IntPtr p;
                int bytes;
                if (!LerAmostra(out buf, out p, out bytes)) return null;
                return Separar(p, bytes);
            }
            catch (Exception ex) { Falha = ex.Message; return null; }
            finally
            {
                if (buf != null) { try { Marshal.ReleaseComObject(buf); } catch { } }
            }
        }

        /// <summary>
        /// Próximo bloco em amostras **inteiras**, exatamente como estão no
        /// arquivo. É o que a análise do arquivo inteiro (onda, loudness,
        /// espectrograma) consome.
        ///
        /// Devolve null no fim do arquivo ou em erro — e só <see cref="Terminou"/>
        /// diz qual dos dois.
        /// </summary>
        public int[][] ProximoBlocoInteiro(out int quadros)
        {
            quadros = 0;
            if (_leitor == null) return null;

            INSSBuffer buf = null;
            try
            {
                IntPtr p;
                int bytes;
                if (!LerAmostra(out buf, out p, out bytes)) return null;

                int bytesPorAmostra = Bits / 8;
                int passo = bytesPorAmostra * Canais;
                quadros = bytes / passo;

                int[][] canais = new int[Canais][];
                for (int c = 0; c < Canais; c++) canais[c] = new int[quadros];

                for (int q = 0; q < quadros; q++)
                    for (int c = 0; c < Canais; c++)
                    {
                        int off = q * passo + c * bytesPorAmostra;
                        int v;
                        if (Bits == 16)
                            v = Marshal.ReadInt16(p, off);
                        else if (Bits == 24)
                        {
                            int b0 = Marshal.ReadByte(p, off);
                            int b1 = Marshal.ReadByte(p, off + 1);
                            int b2 = (sbyte)Marshal.ReadByte(p, off + 2);
                            v = (b2 << 16) | (b1 << 8) | b0;
                        }
                        else if (Bits == 32)
                            v = Marshal.ReadInt32(p, off);
                        else if (Bits == 8)
                            v = Marshal.ReadByte(p, off) - 128;   // PCM de 8 bits e sem sinal
                        else v = 0;
                        canais[c][q] = v;
                    }
                return canais;
            }
            catch (Exception ex) { Falha = ex.Message; return null; }
            finally
            {
                if (buf != null) { try { Marshal.ReleaseComObject(buf); } catch { } }
            }
        }

        private float[][] Separar(IntPtr p, int bytes)
        {
            int bytesPorAmostra = Bits / 8;
            int passo = bytesPorAmostra * Canais;
            if (passo <= 0) return null;
            int quadros = bytes / passo;
            if (quadros <= 0) return null;

            float[][] canais = new float[Canais][];
            for (int c = 0; c < Canais; c++) canais[c] = new float[quadros];

            for (int q = 0; q < quadros; q++)
                for (int c = 0; c < Canais; c++)
                {
                    int off = q * passo + c * bytesPorAmostra;
                    float v;
                    if (Bits == 16)
                        v = Marshal.ReadInt16(p, off) / 32768f;
                    else if (Bits == 24)
                    {
                        int b0 = Marshal.ReadByte(p, off);
                        int b1 = Marshal.ReadByte(p, off + 1);
                        int b2 = (sbyte)Marshal.ReadByte(p, off + 2);
                        v = ((b2 << 16) | (b1 << 8) | b0) / 8388608f;
                    }
                    else if (Bits == 32)
                        v = Marshal.ReadInt32(p, off) / 2147483648f;
                    else v = 0f;
                    canais[c][q] = v;
                }
            return canais;
        }

        /// <summary>
        /// Item 13 da revisão. Por padrão o decodificador do Windows Media
        /// entrega no máximo 16 bits e 2 canais, "seguro para qualquer placa":
        /// WMA Lossless de 24 bits chegaria reduzido, e 5.1 chegaria misturado
        /// em estéreo. EnableDiscreteOutput com SpeakerConfig = 0
        /// (DSSPEAKER_DIRECTOUT) pede o formato do arquivo, canal a canal.
        /// Depois disso o primeiro formato oferecido é o original, e é ele que
        /// se escolhe. Em arquivo de CD (16 bits, estéreo) nada muda.
        /// </summary>
        private void PedirSaidaOriginal(uint saida)
        {
            IntPtr pValor = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(pValor, 1);
                try { _leitor.SetOutputSetting(saida, "EnableDiscreteOutput", TipoBool, pValor, 4); } catch { }
                Marshal.WriteInt32(pValor, 0);
                try { _leitor.SetOutputSetting(saida, "SpeakerConfig", TipoDword, pValor, 4); } catch { }

                uint qtd;
                _leitor.GetOutputFormatCount(saida, out qtd);
                if (qtd == 0) return;
                IWMMediaProps props;
                _leitor.GetOutputFormat(saida, 0, out props);
                if (props == null) return;
                try { _leitor.SetOutputProps(saida, props); }
                finally { try { Marshal.ReleaseComObject(props); } catch { } }
            }
            catch { }   // sem saída original, fica a padrão — e LerFormato diz qual é
            finally { Marshal.FreeHGlobal(pValor); }
        }

        private const int TipoDword = 0;   // WMT_TYPE_DWORD
        private const int TipoBool = 2;    // WMT_TYPE_BOOL

        private bool LerFormato(uint saida)
        {
            IWMMediaProps props = null;
            IntPtr bloco = IntPtr.Zero;
            try
            {
                _leitor.GetOutputProps(saida, out props);
                if (props == null) return false;

                uint tam = 0;
                props.GetMediaType(IntPtr.Zero, ref tam);
                if (tam == 0) return false;

                bloco = Marshal.AllocHGlobal((int)tam);
                if (props.GetMediaType(bloco, ref tam) < 0) return false;

                uint cbFormat = (uint)Marshal.ReadInt32(bloco, OffsetCbFormat);
                IntPtr pbFormat = Marshal.ReadIntPtr(bloco, OffsetPbFormat);
                if (cbFormat < 16 || pbFormat == IntPtr.Zero) return false;

                // WAVEFORMATEX. Só PCM inteiro (1) ou WAVE_FORMAT_EXTENSIBLE
                // (0xFFFE, que é como chega o PCM de 24 bits/multicanal): outro
                // tipo (float, comprimido) seria lido como inteiro e viraria lixo.
                int intTag = (ushort)Marshal.ReadInt16(pbFormat, 0);
                if (intTag != 1 && intTag != 0xFFFE) return false;
                Canais = Marshal.ReadInt16(pbFormat, 2);
                Taxa = Marshal.ReadInt32(pbFormat, 4);
                Bits = Marshal.ReadInt16(pbFormat, 14);
                return Canais > 0 && Taxa > 0 && Bits > 0;
            }
            catch { return false; }
            finally
            {
                if (bloco != IntPtr.Zero) Marshal.FreeHGlobal(bloco);
                if (props != null) { try { Marshal.ReleaseComObject(props); } catch { } }
            }
        }

        public void Fechar()
        {
            if (_leitor != null)
            {
                try { _leitor.Close(); } catch { }
                try { Marshal.ReleaseComObject(_leitor); } catch { }
            }
            _leitor = null;
            Canais = 0; Taxa = 0; Bits = 0;
            FluxosDesligados = 0;
            _intSaidaAudio = -1;
            TempoDoUltimoBloco = 0;
        }

        public void Dispose() { Fechar(); }
    }
}
