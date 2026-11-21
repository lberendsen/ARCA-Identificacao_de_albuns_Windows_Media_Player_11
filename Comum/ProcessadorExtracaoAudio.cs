using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ARCA.Modelo
{
    /// <summary>
    /// Conferência de uma faixa entre o áudio do disco (o que entrou no
    /// codificador) e o WMA que saiu dele.
    /// </summary>
    public sealed class TConferenciaWma
    {
        public int intFaixa;
        public string strArquivoWma;
        public string strHashFonte, strHashWma;
        public long lngBytesFonte, lngBytesWma;
        public string strFalha;
        public bool Confere { get { return strFalha == null && strHashWma != null && strHashWma == strHashFonte; } }
    }

    /// <summary>
    /// Fatiador de imagem BIN/CUE gerada por SPTI em arquivos WMA Lossless.
    /// Fatiamento exato por setor óptico (2352 bytes/setor, 44.1kHz 16-bit PCM estéreo),
    /// e codificação pelo Windows Media Format do próprio Windows (EscritorWma), com o
    /// PCM indo do .bin direto ao codificador (até 04/10/2026: Expression Encoder 4 sobre WAVs).
    /// Embute tags e capas de alta resolução via EditorAsf/wmvcore.dll.
    /// </summary>
    public static class ProcessadorExtracaoAudio
    {
        public delegate void Relato(string mensagem);
        public delegate void Progresso(int faixaAtual, int totalFaixas, double percentualFaixa);

        public static List<TrackInfo> ParsearCue(string strCaminhoCue)
        {
            string strAlbumCdText;
            string strArtistaCdText;
            long lngUltimoLeadOut;
            return ParsearCue(strCaminhoCue, out strAlbumCdText, out strArtistaCdText, out lngUltimoLeadOut);
        }

        public static List<TrackInfo> ParsearCue(string strCaminhoCue, out string strAlbumCdText, out string strArtistaCdText)
        {
            long lngUltimoLeadOut;
            return ParsearCue(strCaminhoCue, out strAlbumCdText, out strArtistaCdText, out lngUltimoLeadOut);
        }

        public static List<TrackInfo> ParsearCue(string strCaminhoCue, out string strAlbumCdText, out string strArtistaCdText, out long lngUltimoLeadOutSessao1Bytes)
        {
            strAlbumCdText = null;
            strArtistaCdText = null;
            lngUltimoLeadOutSessao1Bytes = 0;
            List<TrackInfo> lstFaixas = new List<TrackInfo>();
            if (!File.Exists(strCaminhoCue)) return lstFaixas;

            string[] arrLinhasCue = File.ReadAllLines(strCaminhoCue);
            int intSessaoAtual = 1;
            string strMcnCue = null;

            foreach (string linha in arrLinhasCue)
            {
                string t = linha.Trim();
                if (t.StartsWith("REM SESSION", StringComparison.OrdinalIgnoreCase))
                {
                    Match mSessao = Regex.Match(t, @"REM\s+SESSION\s+(\d+)", RegexOptions.IgnoreCase);
                    if (mSessao.Success) intSessaoAtual = int.Parse(mSessao.Groups[1].Value);
                }
                else if (t.StartsWith("REM LEAD-OUT", StringComparison.OrdinalIgnoreCase))
                {
                    Match mLeadOut = Regex.Match(t, @"REM\s+LEAD-OUT\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
                    if (mLeadOut.Success)
                    {
                        int min = int.Parse(mLeadOut.Groups[1].Value);
                        int sec = int.Parse(mLeadOut.Groups[2].Value);
                        int frames = int.Parse(mLeadOut.Groups[3].Value);
                        long totalFrames = (min * 60 * 75) + (sec * 75) + frames;
                        if (intSessaoAtual == 1)
                        {
                            lngUltimoLeadOutSessao1Bytes = totalFrames * 2352;
                        }
                    }
                }
                else if (t.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    Match m = Regex.Match(t, @"TRACK\s+(\d+)\s+(\S+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        TrackInfo ti = new TrackInfo();
                        ti.TrackNum = int.Parse(m.Groups[1].Value);
                        ti.TrackType = m.Groups[2].Value;
                        ti.SessionNumber = intSessaoAtual;
                        ti.Title = string.Format("Faixa {0:D2}", ti.TrackNum);
                        ti.Artist = strArtistaCdText;
                        ti.ISRC = "";
                        lstFaixas.Add(ti);
                    }
                }
                else if (lstFaixas.Count == 0)
                {
                    if (t.StartsWith("CATALOG", StringComparison.OrdinalIgnoreCase))
                    {
                        Match mCat = Regex.Match(t, @"CATALOG\s+(\S+)", RegexOptions.IgnoreCase);
                        if (mCat.Success) strMcnCue = mCat.Groups[1].Value.Trim();
                    }
                    else if (t.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"^TITLE\s+(?:""([^""]+)""|(.+))$", RegexOptions.IgnoreCase);
                        if (m.Success) strAlbumCdText = (!string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    }
                    else if (t.StartsWith("PERFORMER", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"^PERFORMER\s+(?:""([^""]+)""|(.+))$", RegexOptions.IgnoreCase);
                        if (m.Success) strArtistaCdText = (!string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    }
                }
                else if (lstFaixas.Count > 0)
                {
                    TrackInfo currentInfo = lstFaixas[lstFaixas.Count - 1];
                    if (t.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"^TITLE\s+(?:""([^""]+)""|(.+))$", RegexOptions.IgnoreCase);
                        if (m.Success) currentInfo.Title = (!string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    }
                    else if (t.StartsWith("PERFORMER", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"^PERFORMER\s+(?:""([^""]+)""|(.+))$", RegexOptions.IgnoreCase);
                        if (m.Success) currentInfo.Artist = (!string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    }
                    else if (t.StartsWith("SONGWRITER", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"^SONGWRITER\s+(?:""([^""]+)""|(.+))$", RegexOptions.IgnoreCase);
                        if (m.Success) currentInfo.Composer = (!string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    }
                    else if (t.StartsWith("ISRC", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"ISRC\s+(\S+)", RegexOptions.IgnoreCase);
                        if (m.Success) currentInfo.ISRC = m.Groups[1].Value.Trim();
                    }
                    else if (t.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase))
                    {
                        Match m = Regex.Match(t, @"INDEX 01 (\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            int min = int.Parse(m.Groups[1].Value);
                            int sec = int.Parse(m.Groups[2].Value);
                            int frames = int.Parse(m.Groups[3].Value);
                            long totalFrames = (min * 60 * 75) + (sec * 75) + frames;
                            currentInfo.StartLba = totalFrames;
                            currentInfo.ByteOffset = totalFrames * 2352;
                        }
                    }
                }
            }

            foreach (TrackInfo track in lstFaixas)
            {
                if (string.IsNullOrEmpty(track.Artist) && !string.IsNullOrEmpty(strArtistaCdText))
                {
                    track.Artist = strArtistaCdText;
                }
                if (!string.IsNullOrEmpty(strAlbumCdText))
                {
                    track.PossibleAlbums.Add(strAlbumCdText);
                }
            }

            return lstFaixas;
        }

        /// <summary>
        /// Copia lngTamanho bytes do .bin a partir de lngInicio (que pode cair
        /// antes do início ou depois do fim do disco: aí entra silêncio) e
        /// devolve o SHA-256 do que foi escrito — a assinatura do áudio que
        /// entra no codificador.
        /// </summary>
        private static string CopiarCorrigido(string strBin, long lngTamanhoBin, long lngInicio, long lngTamanho, Stream objDestino)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fsBin = new FileStream(strBin, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buf = new byte[65536];
                long pos = lngInicio, fim = lngInicio + lngTamanho;
                while (pos < fim)
                {
                    int n = (int)Math.Min(buf.Length, fim - pos);
                    if (pos < 0 || pos >= lngTamanhoBin)
                    {
                        // Fora do disco: silêncio até a borda mais próxima.
                        long lngAteBorda = pos < 0 ? -pos : fim - pos;
                        n = (int)Math.Min(n, lngAteBorda);
                        Array.Clear(buf, 0, n);
                    }
                    else
                    {
                        n = (int)Math.Min(n, lngTamanhoBin - pos);
                        fsBin.Seek(pos, SeekOrigin.Begin);
                        int lidos = 0;
                        while (lidos < n) { int r = fsBin.Read(buf, lidos, n - lidos); if (r <= 0) break; lidos += r; }
                        if (lidos < n) Array.Clear(buf, lidos, n - lidos);
                    }
                    objDestino.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    pos += n;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Decodifica o WMA e compara tamanho e SHA-256 com os do áudio de origem.</summary>
        public static void ConferirWma(string strWma, TConferenciaWma c)
        {
            c.strFalha = null;
            c.strArquivoWma = strWma;
            if (string.IsNullOrEmpty(strWma) || !File.Exists(strWma)) { c.strFalha = "o WMA não foi gerado"; return; }
            using (ARCA.Interop.LeitorAudio l = new ARCA.Interop.LeitorAudio())
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                if (!l.Abrir(strWma)) { c.strFalha = "não consegui abrir o WMA: " + l.Falha; return; }
                if (l.Canais != 2 || l.Bits != 16 || l.Taxa != 44100)
                {
                    c.strFalha = string.Format("o WMA saiu em {0} canais, {1} bits, {2} Hz", l.Canais, l.Bits, l.Taxa);
                    return;
                }
                long n = 0;
                byte[] b;
                while ((b = l.ProximoBlocoBytes()) != null) { sha.TransformBlock(b, 0, b.Length, null, 0); n += b.Length; }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (!l.Terminou) { c.strFalha = "erro ao decodificar o WMA: " + l.Falha; return; }
                c.lngBytesWma = n;
                c.strHashWma = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (n != c.lngBytesFonte)
                    c.strFalha = string.Format("tamanho diferente ({0:+#;-#} bytes)", n - c.lngBytesFonte);
                else if (c.strHashWma != c.strHashFonte)
                    c.strFalha = "mesmo tamanho, conteúdo diferente";
            }
        }

        /// <summary>Título da faixa 00: o áudio escondido antes da faixa 1 (HTOA).</summary>
        public const string TituloFaixaOculta = "Faixa oculta (antes da faixa 1)";

        /// <summary>Mínimo para o trecho antes da faixa 1 virar faixa: 1 s.</summary>
        public const int SetoresMinimosFaixaOculta = 75;

        /// <summary>
        /// O trecho do .bin tem som de verdade? Pico acima de −60 dBFS
        /// (amplitude 32 em 16 bits): silêncio digital e ruído de fundo de
        /// fita ficam abaixo; música, mesmo baixinha, passa. Fora do disco
        /// (offset negativo) conta como silêncio.
        /// </summary>
        public static bool TrechoTemSom(string strBin, long lngInicio, long lngTamanho, out double dblPicoDb)
        {
            int intPico = 0;
            using (FileStream fs = new FileStream(strBin, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long a = Math.Max(0, lngInicio) & ~3L, b = Math.Min(fs.Length, lngInicio + lngTamanho);
                fs.Seek(a, SeekOrigin.Begin);
                byte[] buf = new byte[1 << 16];
                long pos = a;
                while (pos < b)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, b - pos)) & ~1;
                    if (n <= 0) break;
                    for (int i = 0; i + 1 < n; i += 2)
                    {
                        int v = Math.Abs((int)(short)(buf[i] | (buf[i + 1] << 8)));
                        if (v > intPico) intPico = v;
                    }
                    pos += n;
                }
            }
            dblPicoDb = intPico > 0 ? 20.0 * Math.Log10(intPico / 32768.0) : double.NegativeInfinity;
            return intPico > 32;
        }

        /// <summary>
        /// Um Stream que entrega ao EscritorWma o que o CopiarCorrigido
        /// escreveria num WAV — é o que dispensa o arquivo temporário. Falha
        /// do codificador vira IOException, que interrompe a cópia.
        ///
        /// O CopiarCorrigido escreve blocos de 64 KB (múltiplos de 4); um resto
        /// de quadro incompleto, se aparecesse, espera o bloco seguinte.
        /// </summary>
        private sealed class TFluxoParaEscritor : Stream
        {
            private readonly ARCA.Interop.EscritorWma m_objEscritor;
            private readonly long m_lngTotal;
            private readonly Action<double> m_actProgresso;
            private readonly byte[] m_arrResto = new byte[4];
            private int m_intResto;
            private long m_lngEscritos;
            private int m_intUltimoPct = -1;

            public TFluxoParaEscritor(ARCA.Interop.EscritorWma objEscritor, long lngTotal, Action<double> actProgresso)
            {
                m_objEscritor = objEscritor;
                m_lngTotal = lngTotal;
                m_actProgresso = actProgresso;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                // Completa o quadro que ficou pela metade no bloco anterior.
                while (m_intResto > 0 && m_intResto < 4 && count > 0)
                {
                    m_arrResto[m_intResto++] = buffer[offset++];
                    count--;
                }
                if (m_intResto == 4) { Entregar(m_arrResto, 0, 4); m_intResto = 0; }

                int intInteiros = count - count % 4;
                if (intInteiros > 0) Entregar(buffer, offset, intInteiros);
                for (int i = intInteiros; i < count; i++) m_arrResto[m_intResto++] = buffer[offset + i];
            }

            private void Entregar(byte[] arr, int intInicio, int intBytes)
            {
                if (!m_objEscritor.Escrever(arr, intInicio, intBytes))
                    throw new IOException(m_objEscritor.Falha ?? "o codificador recusou o áudio");
                m_lngEscritos += intBytes;
                if (m_actProgresso != null && m_lngTotal > 0)
                {
                    int intPct = (int)(m_lngEscritos * 100 / m_lngTotal);
                    if (intPct != m_intUltimoPct) { m_intUltimoPct = intPct; m_actProgresso(intPct); }
                }
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return m_lngEscritos; } }
            public override long Position { get { return m_lngEscritos; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        public static bool FatiarEConverterParaWma(
            string strCaminhoCue,
            string strArtistaEleito,
            string strAlbumEleito,
            string strAnoEleito,
            string strGeneroEleito,
            byte[] arrCapaOficialBytes,
            Relato relatar,
            Progresso notificarProgresso,
            int intOffsetAmostras = 0,
            List<TConferenciaWma> lstConferencia = null)
        {
            if (relatar == null) relatar = (s) => { };

            if (!File.Exists(strCaminhoCue))
            {
                relatar("Erro: Arquivo CUE não encontrado: " + strCaminhoCue);
                return false;
            }

            string strPasta = Path.GetDirectoryName(strCaminhoCue);
            string strAlbumCdText, strArtistaCdText;
            long lngUltimoLeadOutSessao1Bytes;
            List<TrackInfo> lstTodasFaixas = ParsearCue(strCaminhoCue, out strAlbumCdText, out strArtistaCdText, out lngUltimoLeadOutSessao1Bytes);

            List<TrackInfo> lstFaixasAudio = new List<TrackInfo>();
            List<TrackInfo> lstFaixasDados = new List<TrackInfo>();
            foreach (TrackInfo ti in lstTodasFaixas)
            {
                if (string.Equals(ti.TrackType, "AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    lstFaixasAudio.Add(ti);
                }
                else if (lstFaixasAudio.Count > 0)
                {
                    // Só dados DEPOIS do áudio são sessão de CD Extra. Dados antes
                    // (CD mixed-mode) nem foram lidos — no .bin são zeros.
                    lstFaixasDados.Add(ti);
                }
            }

            if (lstFaixasAudio.Count == 0)
            {
                relatar("Erro: Nenhuma faixa de áudio encontrada no CUE.");
                return false;
            }

            string strNomeBin = Path.GetFileNameWithoutExtension(strCaminhoCue) + ".bin";
            string strCaminhoBin = Path.Combine(strPasta, strNomeBin);
            if (!File.Exists(strCaminhoBin))
            {
                strCaminhoBin = Path.Combine(strPasta, "AudioCD.bin");
            }
            if (!File.Exists(strCaminhoBin))
            {
                relatar("Erro: Arquivo BIN de áudio correspondente não encontrado: " + strCaminhoBin);
                return false;
            }

            long lngTamanhoTotalBin = new FileInfo(strCaminhoBin).Length;

            // O fim do áudio vem do mesmo índice que o AccurateRip usa: em CD
            // Extra, 11.400 setores antes da faixa de dados (o intervalo entre
            // as sessões não é música), e não no início dela.
            TTocAudio objToc = TTocAudio.DeCue(strCaminhoCue, lngTamanhoTotalBin);

            for (int i = 0; i < lstFaixasAudio.Count; i++)
            {
                long startByte = lstFaixasAudio[i].ByteOffset;
                long endByte = 0;
                if (i + 1 < lstFaixasAudio.Count)
                {
                    endByte = lstFaixasAudio[i + 1].ByteOffset;
                }
                else if (objToc != null && (long)objToc.intLeadOut * 2352 > startByte)
                {
                    endByte = (long)objToc.intLeadOut * 2352;
                }
                else if (lngUltimoLeadOutSessao1Bytes > startByte)
                {
                    endByte = lngUltimoLeadOutSessao1Bytes;
                }
                else if (lstFaixasDados.Count > 0)
                {
                    endByte = lstFaixasDados[0].ByteOffset;
                }
                else
                {
                    endByte = lngTamanhoTotalBin;
                }
                lstFaixasAudio[i].EndByteOffset = endByte;
            }
            if (intOffsetAmostras != 0)
                relatar(string.Format("Corrigindo o offset do drive: {0:+0;-0} amostras.", intOffsetAmostras));

            // Áudio escondido antes da faixa 1 (HTOA): só existe se a faixa 1 começa
            // depois do início do disco — e não em mixed-mode, onde o que vem antes
            // é a faixa de dados. Com som de verdade, vira a faixa 00; silêncio, não.
            if (lstFaixasAudio.Count > 0 && (objToc == null || objToc.intDadosAntes < 0)
                && lstFaixasAudio[0].ByteOffset >= (long)SetoresMinimosFaixaOculta * 2352)
            {
                long lngFimOculta = lstFaixasAudio[0].ByteOffset;
                double dblPico;
                if (TrechoTemSom(strCaminhoBin, (long)intOffsetAmostras * 4, lngFimOculta, out dblPico))
                {
                    lstFaixasAudio.Insert(0, new TrackInfo
                    {
                        TrackNum = 0,
                        ByteOffset = 0,
                        EndByteOffset = lngFimOculta,
                        StartLba = 0,
                        Title = TituloFaixaOculta,
                        TrackType = "AUDIO",
                        ISRC = ""
                    });
                    relatar(string.Format("Áudio escondido antes da faixa 1: {0:F1} s com som (pico {1:F1} dBFS). Vira a faixa 00.",
                        lngFimOculta / 2352.0 / 75.0, dblPico));
                }
                else
                    relatar(string.Format("Antes da faixa 1 há {0:F1} s de silêncio: não vira faixa.", lngFimOculta / 2352.0 / 75.0));
            }

            relatar(string.Format("Iniciando fatiamento de {0} faixas de áudio a partir da imagem física...", lstFaixasAudio.Count));

            // Codificação pelo Windows Media Format (EscritorWma), desde
            // 04/10/2026 — antes, Expression Encoder 4 sobre WAVs temporários.
            // O codec é o mesmo (o Encoder usava o wmvcore.dll do Windows); o
            // PCM agora vai do .bin direto para o codificador, sem WAV.
            relatar("Codificando áudio para Windows Media Audio 9 Lossless (Bit-Perfect)...");
            for (int i = 0; i < lstFaixasAudio.Count; i++)
            {
                TrackInfo faixaAtual = lstFaixasAudio[i];
                long startByte = faixaAtual.ByteOffset;
                long length = faixaAtual.EndByteOffset - startByte;

                if (length <= 0) continue;

                string strTituloBase = !string.IsNullOrEmpty(faixaAtual.Title) ? faixaAtual.Title : string.Format("Faixa {0:D2}", faixaAtual.TrackNum);
                string strNomeArquivoBase = string.Format("{0:D2} - {1}", faixaAtual.TrackNum, strTituloBase);

                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    strNomeArquivoBase = strNomeArquivoBase.Replace(c.ToString(), "");
                }
                strNomeArquivoBase = strNomeArquivoBase.Trim();
                faixaAtual.WmaFilePath = Path.Combine(strPasta, strNomeArquivoBase + ".wma");

                // As mesmas tags que o Encoder gravava, com os mesmos tipos
                // (WM/TrackNumber é DWORD). Vazias ficam de fora.
                string strArtistaFinal = !string.IsNullOrEmpty(faixaAtual.Artist) ? faixaAtual.Artist : strArtistaEleito;
                List<KeyValuePair<string, object>> lstAtributos = new List<KeyValuePair<string, object>>
                {
                    new KeyValuePair<string, object>("Title", strTituloBase),
                    new KeyValuePair<string, object>("Author", strArtistaFinal),
                    new KeyValuePair<string, object>("WM/AlbumArtist", strArtistaEleito),
                    new KeyValuePair<string, object>("WM/AlbumTitle", strAlbumEleito),
                    new KeyValuePair<string, object>("WM/Year", strAnoEleito),
                    new KeyValuePair<string, object>("WM/Genre", strGeneroEleito),
                    new KeyValuePair<string, object>("WM/TrackNumber", faixaAtual.TrackNum),
                    new KeyValuePair<string, object>("WM/EncodedBy", "ARCA / Windows Media Format / WMA 9 Lossless")
                };

                string strFalhaFaixa = null;
                string strHashFonte = null;
                using (ARCA.Interop.EscritorWma objEscritor = new ARCA.Interop.EscritorWma())
                {
                    if (!objEscritor.Abrir(faixaAtual.WmaFilePath, lstAtributos))
                        strFalhaFaixa = objEscritor.Falha;
                    else
                    {
                        if (i == 0 && !string.IsNullOrEmpty(objEscritor.Formato)) relatar("Formato do codec: " + objEscritor.Formato + ".");
                        int intFaixa = faixaAtual.TrackNum;
                        using (TFluxoParaEscritor objFluxo = new TFluxoParaEscritor(objEscritor, length, delegate(double dblPct)
                        {
                            if (notificarProgresso != null) notificarProgresso(intFaixa, lstFaixasAudio.Count, dblPct);
                        }))
                        {
                            // A faixa corrigida começa offset amostras adiante na
                            // leitura crua. Antes do início ou depois do fim do
                            // disco não há o que ler: silêncio, como em qualquer
                            // ripador sem leitura além do lead-out — e são
                            // exatamente as pontas que o AccurateRip ignora.
                            try
                            {
                                strHashFonte = CopiarCorrigido(strCaminhoBin, lngTamanhoTotalBin,
                                                               startByte + (long)intOffsetAmostras * 4, length, objFluxo);
                            }
                            catch (IOException exEscrita) { strFalhaFaixa = exEscrita.Message; }
                        }
                        if (strFalhaFaixa == null && !objEscritor.Concluir()) strFalhaFaixa = objEscritor.Falha;
                    }
                }

                if (strFalhaFaixa != null)
                {
                    // WMA pela metade não fica na área de trabalho parecendo bom.
                    try { if (File.Exists(faixaAtual.WmaFilePath)) File.Delete(faixaAtual.WmaFilePath); } catch { }
                    relatar(string.Format("Erro: a codificação da faixa {0:D2} falhou: {1}", faixaAtual.TrackNum, strFalhaFaixa));
                    return false;
                }
                if (lstConferencia != null)
                    lstConferencia.Add(new TConferenciaWma { intFaixa = faixaAtual.TrackNum, strHashFonte = strHashFonte, lngBytesFonte = length });
            }

            // Elo 2 do bit-perfect: o WMA decodificado tem de ser,
            // byte a byte, o PCM que entrou no codificador.
            if (lstConferencia != null)
            {
                relatar("Conferindo cada WMA contra o áudio do disco, bit a bit...");
                foreach (TrackInfo ti in lstFaixasAudio)
                {
                    TConferenciaWma c = lstConferencia.Find(x => x.intFaixa == ti.TrackNum);
                    if (c == null) continue;
                    ConferirWma(ti.WmaFilePath, c);
                    relatar(c.Confere
                        ? string.Format("  Faixa {0:D2}: WMA idêntico ao áudio do disco.", c.intFaixa)
                        : string.Format("  ⚠ Faixa {0:D2}: WMA DIFERENTE do áudio do disco — {1}", c.intFaixa, c.strFalha));
                }
            }

            relatar("Codificação WMA Lossless concluída. Aplicando metadados nativos e capa...");

            foreach (TrackInfo ti in lstFaixasAudio)
            {
                if (!File.Exists(ti.WmaFilePath)) continue;

                try
                {
                    using (ARCA.Interop.EditorAsf editor = new ARCA.Interop.EditorAsf())
                    {
                        if (editor.Abrir(ti.WmaFilePath))
                        {
                            if (!string.IsNullOrEmpty(ti.ISRC))
                            {
                                editor.DefinirTexto("WM/ISRC", ti.ISRC);
                            }
                            if (!string.IsNullOrEmpty(ti.Composer))
                            {
                                editor.DefinirTexto("WM/Composer", ti.Composer);
                            }
                            if (arrCapaOficialBytes != null && arrCapaOficialBytes.Length > 0)
                            {
                                editor.DefinirImagem(arrCapaOficialBytes, null, "Cover");
                            }
                            editor.Salvar();
                        }
                    }
                }
                catch (Exception exTags)
                {
                    relatar(string.Format("Aviso: Falha ao embutir atributos complementares na faixa {0}: {1}", ti.TrackNum, exTags.Message));
                }
            }

            // 3. CD Extra: os vídeos da sessão de dados NÃO saem do .bin — ele
            //    termina no fim do áudio. Quem chama os copia da unidade montada
            //    (TCdExtraExtractor.CopiarVideosDaUnidade), conferidos.
            if (lstFaixasDados.Count > 0)
                relatar(string.Format("Enhanced CD (CD Extra): {0} faixas de áudio e {1} faixa(s) de dados; os vídeos vêm da unidade.",
                    lstFaixasAudio.Count, lstFaixasDados.Count));

            relatar("Processamento e fatiamento concluídos com sucesso.");
            return true;
        }

        public static bool RenomearETaggearFaixas(
            string strPastaTrabalho,
            List<string> lstTitulos,
            List<string> lstArtistasFaixas,
            string strArtistaAlbum,
            string strAlbum,
            byte[] arrCapaBytes,
            Relato relatar)
        {
            return RenomearETaggearFaixas(strPastaTrabalho, lstTitulos, lstArtistasFaixas, strArtistaAlbum, strAlbum, null, arrCapaBytes, relatar);
        }

        /// <summary>
        /// Renomeia os arquivos WMA fatiados na pasta de trabalho para o formato canônico
        /// (ex: '01 - Nome da Faixa.wma') e atualiza os cabeçalhos ASF com Title, Author, Album, Composer e Capa.
        /// </summary>
        public static bool RenomearETaggearFaixas(
            string strPastaTrabalho,
            List<string> lstTitulos,
            List<string> lstArtistasFaixas,
            string strArtistaAlbum,
            string strAlbum,
            List<string> lstCompositores,
            byte[] arrCapaBytes,
            Relato relatar)
        {
            return RenomearETaggearFaixas(strPastaTrabalho, lstTitulos, lstArtistasFaixas, strArtistaAlbum,
                strAlbum, lstCompositores, arrCapaBytes, null, null, relatar);
        }

        /// <summary>
        /// Igual à versão acima, gravando também ano (WM/Year) e gênero
        /// (WM/Genre) quando informados — vazios não apagam o que o arquivo já tem.
        /// </summary>
        public static bool RenomearETaggearFaixas(
            string strPastaTrabalho,
            List<string> lstTitulos,
            List<string> lstArtistasFaixas,
            string strArtistaAlbum,
            string strAlbum,
            List<string> lstCompositores,
            byte[] arrCapaBytes,
            string strAno,
            string strGenero,
            Relato relatar)
        {
            if (relatar == null) relatar = delegate { };

            if (string.IsNullOrEmpty(strPastaTrabalho) || !Directory.Exists(strPastaTrabalho))
            {
                relatar("Erro: Pasta de trabalho não encontrada.");
                return false;
            }

            if (lstTitulos == null || lstTitulos.Count == 0)
            {
                relatar("Aviso: Nenhuma lista de títulos informada para renomeação.");
                return false;
            }

            DirectoryInfo dirInfo = new DirectoryInfo(strPastaTrabalho);
            FileInfo[] arrArquivos = dirInfo.GetFiles("*.wma");
            if (arrArquivos == null || arrArquivos.Length == 0)
            {
                relatar("Aviso: Nenhum arquivo WMA localizado na área de trabalho para renomear.");
                return false;
            }

            // Agrupa e ordena os arquivos pelo número da faixa indicado no início do nome
            List<KeyValuePair<int, FileInfo>> lstFaixasWma = new List<KeyValuePair<int, FileInfo>>();
            foreach (FileInfo fi in arrArquivos)
            {
                Match m = Regex.Match(fi.Name, @"^(\d{1,3})\s*[-_.]");
                if (m.Success)
                {
                    int intNum = int.Parse(m.Groups[1].Value);
                    lstFaixasWma.Add(new KeyValuePair<int, FileInfo>(intNum, fi));
                }
            }

            if (lstFaixasWma.Count == 0)
            {
                relatar("Aviso: Nenhum arquivo WMA com numeração inicial padrão localizado.");
                return false;
            }

            lstFaixasWma.Sort((a, b) => a.Key.CompareTo(b.Key));

            relatar(string.Format("Aplicando metadados e renomeando {0} faixa(s) com base na identificação oficial...", lstFaixasWma.Count));

            char[] arrInvalidos = Path.GetInvalidFileNameChars();

            for (int i = 0; i < lstFaixasWma.Count; i++)
            {
                int intNumFaixa = lstFaixasWma[i].Key;
                FileInfo fiAtual = lstFaixasWma[i].Value;

                // A faixa 00 (áudio escondido antes da faixa 1) não está na lista
                // de títulos: pela posição, receberia o título da faixa 1. Leva
                // só as etiquetas do álbum.
                if (intNumFaixa == 0)
                {
                    try
                    {
                        using (ARCA.Interop.EditorAsf editor = new ARCA.Interop.EditorAsf())
                        {
                            if (editor.Abrir(fiAtual.FullName))
                            {
                                if (!string.IsNullOrEmpty(strArtistaAlbum)) { editor.DefinirTexto("Author", strArtistaAlbum); editor.DefinirTexto("WM/AlbumArtist", strArtistaAlbum); }
                                if (!string.IsNullOrEmpty(strAlbum)) editor.DefinirTexto("WM/AlbumTitle", strAlbum);
                                if (!string.IsNullOrEmpty(strAno)) editor.DefinirTexto("WM/Year", strAno.Trim());
                                if (!string.IsNullOrEmpty(strGenero)) editor.DefinirTexto("WM/Genre", strGenero.Trim());
                                if (arrCapaBytes != null && arrCapaBytes.Length > 0) editor.DefinirImagem(arrCapaBytes, null, "Cover");
                                editor.Salvar();
                            }
                        }
                    }
                    catch (Exception exOculta) { relatar("Aviso: Falha ao atualizar as tags da faixa 00: " + exOculta.Message); }
                    continue;
                }

                // Encontra o título correspondente (pelo índice 0-based da contagem ou pela posição da faixa)
                int intIdxTitulo = (intNumFaixa >= 1 && intNumFaixa <= lstTitulos.Count) ? (intNumFaixa - 1) : i;
                if (intIdxTitulo < 0 || intIdxTitulo >= lstTitulos.Count) continue;

                string strTituloOficial = lstTitulos[intIdxTitulo];
                if (string.IsNullOrWhiteSpace(strTituloOficial)) continue;

                strTituloOficial = strTituloOficial.Trim();

                // Higieniza o nome para o sistema de arquivos
                string strTituloLimpo = strTituloOficial;
                foreach (char c in arrInvalidos)
                {
                    strTituloLimpo = strTituloLimpo.Replace(c.ToString(), "");
                }
                strTituloLimpo = Regex.Replace(strTituloLimpo, @"\s+", " ").Trim();
                if (string.IsNullOrEmpty(strTituloLimpo)) strTituloLimpo = string.Format("Faixa {0:D2}", intNumFaixa);

                string strNovoNomeArquivo = string.Format("{0:D2} - {1}.wma", intNumFaixa, strTituloLimpo);
                string strCaminhoAtual = fiAtual.FullName;
                string strNovoCaminho = Path.Combine(strPastaTrabalho, strNovoNomeArquivo);

                // Determina o intérprete específico da faixa (para coletâneas)
                string strArtistaFaixa = (lstArtistasFaixas != null && intIdxTitulo < lstArtistasFaixas.Count && !string.IsNullOrEmpty(lstArtistasFaixas[intIdxTitulo]))
                    ? lstArtistasFaixas[intIdxTitulo].Trim()
                    : strArtistaAlbum;

                // 1. Atualiza as tags ASF nativas
                try
                {
                    using (ARCA.Interop.EditorAsf editor = new ARCA.Interop.EditorAsf())
                    {
                        if (editor.Abrir(strCaminhoAtual))
                        {
                            editor.DefinirTexto("Title", strTituloOficial);
                            if (!string.IsNullOrEmpty(strArtistaFaixa)) editor.DefinirTexto("Author", strArtistaFaixa);
                            if (!string.IsNullOrEmpty(strArtistaAlbum)) editor.DefinirTexto("WM/AlbumArtist", strArtistaAlbum);
                            if (!string.IsNullOrEmpty(strAlbum)) editor.DefinirTexto("WM/AlbumTitle", strAlbum);
                            if (!string.IsNullOrEmpty(strAno)) editor.DefinirTexto("WM/Year", strAno.Trim());
                            if (!string.IsNullOrEmpty(strGenero)) editor.DefinirTexto("WM/Genre", strGenero.Trim());
                            if (lstCompositores != null && intIdxTitulo >= 0 && intIdxTitulo < lstCompositores.Count && !string.IsNullOrEmpty(lstCompositores[intIdxTitulo]))
                            {
                                editor.DefinirTexto("WM/Composer", lstCompositores[intIdxTitulo].Trim());
                            }
                            if (arrCapaBytes != null && arrCapaBytes.Length > 0)
                            {
                                editor.DefinirImagem(arrCapaBytes, null, "Cover");
                            }
                            editor.Salvar();
                        }
                    }
                }
                catch (Exception exAsf)
                {
                    relatar(string.Format("Aviso: Falha ao atualizar tags ASF da faixa {0}: {1}", intNumFaixa, exAsf.Message));
                }

                // 2. Renomeia o arquivo físico se o nome divergir
                // Dois casos que antes se confundiam: o nome novo difere só em
                // maiúsculas (o NTFS acha que é o mesmo arquivo: passa por um
                // nome temporário), ou já existe OUTRO arquivo com esse nome
                // (aí não se mexe em nada: a faixa ficava presa como
                // .tmp_rename, fora do manifesto, e não chegava ao álbum).
                if (!string.Equals(fiAtual.Name, strNovoNomeArquivo, StringComparison.Ordinal))
                {
                    bool blnSoMaiusculas = string.Equals(fiAtual.Name, strNovoNomeArquivo, StringComparison.OrdinalIgnoreCase);
                    string strTempCaminho = strCaminhoAtual + ".tmp_rename";
                    try
                    {
                        if (blnSoMaiusculas)
                        {
                            if (File.Exists(strTempCaminho)) File.Delete(strTempCaminho);
                            File.Move(strCaminhoAtual, strTempCaminho);
                            File.Move(strTempCaminho, strNovoCaminho);
                            relatar(string.Format("  -> Faixa {0:D2}: '{1}' => '{2}'", intNumFaixa, fiAtual.Name, strNovoNomeArquivo));
                        }
                        else if (File.Exists(strNovoCaminho))
                        {
                            relatar(string.Format("ATENÇÃO: não renomeei a faixa {0:D2} ('{1}'): já existe outro arquivo chamado '{2}' na área de trabalho. " +
                                                  "Os dois continuam lá; confira qual é o certo antes de aprovar.",
                                                  intNumFaixa, fiAtual.Name, strNovoNomeArquivo));
                        }
                        else
                        {
                            File.Move(strCaminhoAtual, strNovoCaminho);
                            relatar(string.Format("  -> Faixa {0:D2}: '{1}' => '{2}'", intNumFaixa, fiAtual.Name, strNovoNomeArquivo));
                        }
                    }
                    catch (Exception exMove)
                    {
                        // Parou no meio do nome temporário: devolve o original,
                        // para a faixa não sair do manifesto.
                        try { if (File.Exists(strTempCaminho) && !File.Exists(strCaminhoAtual)) File.Move(strTempCaminho, strCaminhoAtual); } catch { }
                        relatar(string.Format("Erro ao renomear arquivo '{0}' para '{1}': {2}", fiAtual.Name, strNovoNomeArquivo, exMove.Message));
                    }
                }
            }

            // 3. Atualiza o AudioCD.cue correspondente se existir
            string strCaminhoCue = Path.Combine(strPastaTrabalho, "AudioCD.cue");
            if (File.Exists(strCaminhoCue))
            {
                AtualizarCueComTitulos(strCaminhoCue, strArtistaAlbum, strAlbum, lstTitulos, lstArtistasFaixas, lstCompositores, relatar);
            }

            return true;
        }

        public static void AtualizarCueComTitulos(
            string strCaminhoCue,
            string strArtistaAlbum,
            string strAlbum,
            List<string> lstTitulos,
            List<string> lstArtistasFaixas,
            Relato relatar)
        {
            AtualizarCueComTitulos(strCaminhoCue, strArtistaAlbum, strAlbum, lstTitulos, lstArtistasFaixas, null, relatar);
        }

        /// <summary>
        /// Atualiza o arquivo CUE da extração inserindo os títulos oficiais, artistas e compositores nas faixas.
        /// </summary>
        public static void AtualizarCueComTitulos(
            string strCaminhoCue,
            string strArtistaAlbum,
            string strAlbum,
            List<string> lstTitulos,
            List<string> lstArtistasFaixas,
            List<string> lstCompositores,
            Relato relatar)
        {
            if (!File.Exists(strCaminhoCue) || lstTitulos == null || lstTitulos.Count == 0) return;

            try
            {
                string[] arrLinhas = File.ReadAllLines(strCaminhoCue);
                List<string> lstNovasLinhas = new List<string>();
                int intFaixaAtual = 0;
                // O que a faixa corrente já tem. Antes isto se conferia olhando a
                // linha anterior (o TITLE vinha antes do ISRC e era repetido) e o
                // arquivo inteiro (o SONGWRITER da faixa 1 impedia o das outras).
                bool blnFaixaTemTitulo = false, blnFaixaTemCompositor = false;
                bool blnTituloAlbumInserido = false;
                bool blnArtistaAlbumInserido = false;

                for (int i = 0; i < arrLinhas.Length; i++)
                {
                    string strLinha = arrLinhas[i];
                    string strTrim = strLinha.Trim();

                    if (intFaixaAtual == 0)
                    {
                        // Cabeçalho do CUE
                        if (strTrim.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(strAlbum))
                        {
                            lstNovasLinhas.Add(string.Format("TITLE \"{0}\"", strAlbum.Replace("\"", "'")));
                            blnTituloAlbumInserido = true;
                            continue;
                        }
                        if (strTrim.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(strArtistaAlbum))
                        {
                            lstNovasLinhas.Add(string.Format("PERFORMER \"{0}\"", strArtistaAlbum.Replace("\"", "'")));
                            blnArtistaAlbumInserido = true;
                            continue;
                        }

                        if (strTrim.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                        {
                            // Se ainda não tinha TITLE/PERFORMER no topo, insere antes da primeira faixa
                            if (!blnArtistaAlbumInserido && !string.IsNullOrEmpty(strArtistaAlbum))
                            {
                                lstNovasLinhas.Add(string.Format("PERFORMER \"{0}\"", strArtistaAlbum.Replace("\"", "'")));
                                blnArtistaAlbumInserido = true;
                            }
                            if (!blnTituloAlbumInserido && !string.IsNullOrEmpty(strAlbum))
                            {
                                lstNovasLinhas.Add(string.Format("TITLE \"{0}\"", strAlbum.Replace("\"", "'")));
                                blnTituloAlbumInserido = true;
                            }

                            Match mTrk = Regex.Match(strTrim, @"TRACK\s+(\d+)", RegexOptions.IgnoreCase);
                            if (mTrk.Success) intFaixaAtual = int.Parse(mTrk.Groups[1].Value);
                            blnFaixaTemTitulo = blnFaixaTemCompositor = false;
                            lstNovasLinhas.Add(strLinha);
                            continue;
                        }

                        lstNovasLinhas.Add(strLinha);
                    }
                    else
                    {
                        // Dentro de uma faixa
                        if (strTrim.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                        {
                            Match mTrk = Regex.Match(strTrim, @"TRACK\s+(\d+)", RegexOptions.IgnoreCase);
                            if (mTrk.Success) intFaixaAtual = int.Parse(mTrk.Groups[1].Value);
                            blnFaixaTemTitulo = blnFaixaTemCompositor = false;
                            lstNovasLinhas.Add(strLinha);
                            continue;
                        }

                        // Substitui TITLE existente ou PERFORMER existente da faixa
                        if (strTrim.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                        {
                            blnFaixaTemTitulo = true;
                            int intIdx = intFaixaAtual - 1;
                            if (intIdx >= 0 && intIdx < lstTitulos.Count && !string.IsNullOrEmpty(lstTitulos[intIdx]))
                            {
                                lstNovasLinhas.Add(string.Format("    TITLE \"{0}\"", lstTitulos[intIdx].Replace("\"", "'")));
                                continue;
                            }
                        }

                        if (strTrim.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                        {
                            int intIdx = intFaixaAtual - 1;
                            string strArtistaTrk = (lstArtistasFaixas != null && intIdx >= 0 && intIdx < lstArtistasFaixas.Count)
                                ? lstArtistasFaixas[intIdx]
                                : strArtistaAlbum;

                            if (!string.IsNullOrEmpty(strArtistaTrk))
                            {
                                lstNovasLinhas.Add(string.Format("    PERFORMER \"{0}\"", strArtistaTrk.Replace("\"", "'")));
                                continue;
                            }
                        }

                        if (strTrim.StartsWith("SONGWRITER ", StringComparison.OrdinalIgnoreCase))
                        {
                            blnFaixaTemCompositor = true;
                            int intIdx = intFaixaAtual - 1;
                            if (lstCompositores != null && intIdx >= 0 && intIdx < lstCompositores.Count && !string.IsNullOrEmpty(lstCompositores[intIdx]))
                            {
                                lstNovasLinhas.Add(string.Format("    SONGWRITER \"{0}\"", lstCompositores[intIdx].Replace("\"", "'")));
                                continue;
                            }
                        }

                        // Se encontrou INDEX 01 e a faixa não teve TITLE, insere antes do INDEX 01
                        if (strTrim.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase))
                        {
                            int intIdx = intFaixaAtual - 1;
                            if (!blnFaixaTemTitulo && intIdx >= 0 && intIdx < lstTitulos.Count && !string.IsNullOrEmpty(lstTitulos[intIdx]))
                            {
                                lstNovasLinhas.Add(string.Format("    TITLE \"{0}\"", lstTitulos[intIdx].Replace("\"", "'")));
                            }

                            // Verifica se há compositor a inserir
                            if (lstCompositores != null && intIdx >= 0 && intIdx < lstCompositores.Count && !string.IsNullOrEmpty(lstCompositores[intIdx]))
                            {
                                if (!blnFaixaTemCompositor)
                                {
                                    lstNovasLinhas.Add(string.Format("    SONGWRITER \"{0}\"", lstCompositores[intIdx].Replace("\"", "'")));
                                }
                            }
                        }

                        lstNovasLinhas.Add(strLinha);
                    }
                }

                File.WriteAllLines(strCaminhoCue, lstNovasLinhas.ToArray(), System.Text.Encoding.UTF8);
                if (relatar != null) relatar("AudioCD.cue atualizado com os títulos oficiais das faixas.");
            }
            catch (Exception exCue)
            {
                if (relatar != null) relatar("Aviso: Falha ao atualizar AudioCD.cue: " + exCue.Message);
            }
        }
    }
}
