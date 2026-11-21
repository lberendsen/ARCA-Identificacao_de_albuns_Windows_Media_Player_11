using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ARCA.Modelo;

namespace ARCA.Interop
{
    /// <summary>
    /// Orquestrador de extração física direta via SPTI.
    /// Gera os arquivos .BIN/.CUE aplicando resiliência óptica avançada:
    /// - Buffer Binary Halving (redução dinâmica até 1 setor)
    /// - Cache-Defeating (purga de RAM interna do drive)
    /// - Zero-Masking (preenchimento por silêncio em setores irrecuperáveis)
    /// - Geração de CUE compatível com o parser de metadados e conversão do ARCA.
    /// </summary>
    public class SptiImageExtractor
    {
        public delegate void ProgressoExtracao(double dblPercentual, double dblMbLidos, double dblMbTotal, double dblVelocidadeMbS, long lngLbaAtual, long lngLbaTotal);
        public delegate void NotificacaoFase(string strVerbo, string strDetalhe);

        private readonly AcquisitionLogger m_objLogger;

        // Falha de canal: o pedido nem chegou ao disco, ou a unidade deixou de
        // estar pronta. Não é setor ruim — mascarar seria gravar horas de
        // silêncio e chamar isso de extração.
        private const int intTentativasCanal = 5;
        private const int intEsperaCanalMs = 1000;

        // Tantos setores seguidos irrecuperáveis não é risco, é região
        // ilegível: 10 s de áudio. Parar é melhor que mascarar por horas.
        private const int intLimiteMascaradosSeguidos = 10 * TRelatorioExtracao.SetoresPorSegundo;

        /// <summary>Por que ExtrairImagem devolveu false, em frase para a tela.</summary>
        public string MotivoFalha { get; private set; }

        /// <summary>Fabricante e modelo do drive, como ele se declara (INQUIRY). É a chave do offset.</summary>
        public string Fabricante { get; private set; }
        public string Modelo { get; private set; }

        /// <summary>Resultado da releitura de uma faixa.</summary>
        public sealed class TReleitura
        {
            public int intFaixa;
            /// <summary>Setores que vieram diferentes da primeira leitura.</summary>
            public int intSetoresDiferentes;
            /// <summary>Setores que a releitura não conseguiu ler.</summary>
            public int intSetoresIlegiveis;
            public bool Identica { get { return intSetoresDiferentes == 0 && intSetoresIlegiveis == 0; } }
        }

        private const uint SetoresPorBlocoReleitura = 26;    // 61 KB: cabe em qualquer controladora
        private const uint SetoresParaEsvaziarCache = 4000;  // ~9 MB, mais que o cache de drive de CD

        /// <summary>
        /// Lê de novo as faixas pedidas (LBA inicial e final, sem incluir) e
        /// compara setor a setor com o .bin da primeira leitura.
        ///
        /// Não prova a faixa como o AccurateRip prova — um drive que
        /// interpola um trecho ruim pode interpolar igual duas vezes —, mas
        /// pega a leitura instável, que é o erro mais comum. Antes de começar,
        /// lê SetoresParaEsvaziarCache setores longe da primeira faixa: sem
        /// isso a "releitura" poderia vir do cache do drive, e não do disco.
        /// </summary>
        public List<TReleitura> Reler(string strLetraDrive, string strBin, List<int[]> lstFaixas,
                                      Action<int, double> progresso, Func<bool> cancelou, out string strFalha)
        {
            strFalha = null;
            List<TReleitura> lstRes = new List<TReleitura>();
            if (lstFaixas == null || lstFaixas.Count == 0) return lstRes;

            long lngSetoresBin = new FileInfo(strBin).Length / 2352;
            using (SptiDiscReader objLeitor = new SptiDiscReader(m_objLogger))
            using (FileStream fsBin = new FileStream(strBin, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!objLeitor.AbrirUnidade(strLetraDrive)) { strFalha = "não consegui abrir a unidade para a releitura"; return null; }

                byte[] arrDisco = new byte[SetoresPorBlocoReleitura * 2352];
                byte[] arrBin = new byte[arrDisco.Length];
                GCHandle hPin = GCHandle.Alloc(arrDisco, GCHandleType.Pinned);
                try
                {
                    // Esvaziar o cache: ler longe de onde a releitura começa.
                    uint dwPrimeira = (uint)lstFaixas[0][1];
                    uint dwLonge = dwPrimeira < lngSetoresBin / 2 ? (uint)Math.Max(0, lngSetoresBin - SetoresParaEsvaziarCache) : 0;
                    for (uint d = 0; d < SetoresParaEsvaziarCache && dwLonge + d < lngSetoresBin; d += SetoresPorBlocoReleitura)
                    {
                        uint n = Math.Min(SetoresPorBlocoReleitura, (uint)(lngSetoresBin - dwLonge - d));
                        uint b; byte s, k, a, q; int w;
                        objLeitor.LerBlocoSpti(hPin.AddrOfPinnedObject(), dwLonge + d, n, true, out b, out s, out k, out a, out q, out w);
                    }

                    for (int f = 0; f < lstFaixas.Count; f++)
                    {
                        TReleitura r = new TReleitura { intFaixa = lstFaixas[f][0] };
                        uint dwIni = (uint)lstFaixas[f][1], dwFim = (uint)Math.Min(lstFaixas[f][2], lngSetoresBin);
                        for (uint lba = dwIni; lba < dwFim; )
                        {
                            uint n = Math.Min(SetoresPorBlocoReleitura, dwFim - lba);
                            if (!LerComResgate(objLeitor, hPin, lba, ref n))
                            {
                                r.intSetoresIlegiveis++;   // nem um setor: conta e segue
                                lba++;
                                continue;
                            }
                            fsBin.Seek((long)lba * 2352, SeekOrigin.Begin);
                            int intLidos = 0;
                            while (intLidos < n * 2352) { int x = fsBin.Read(arrBin, intLidos, (int)(n * 2352) - intLidos); if (x <= 0) break; intLidos += x; }
                            for (uint sct = 0; sct < n; sct++)
                                for (int i = 0; i < 2352; i++)
                                    if (arrDisco[sct * 2352 + i] != arrBin[sct * 2352 + i]) { r.intSetoresDiferentes++; break; }
                            lba += n;
                            if (progresso != null) progresso(r.intFaixa, (double)(lba - dwIni) / Math.Max(1, dwFim - dwIni) * 100.0);
                            if (cancelou != null && cancelou()) throw new OperationCanceledException();
                        }
                        if (!r.Identica)
                            m_objLogger.RegistrarAviso(string.Format("Releitura da faixa {0}: {1} setor(es) diferentes da primeira leitura, {2} ilegível(is).",
                                r.intFaixa, r.intSetoresDiferentes, r.intSetoresIlegiveis));
                        else
                            m_objLogger.RegistrarDiagnostico(string.Format("Releitura da faixa {0}: idêntica à primeira leitura.", r.intFaixa));
                        lstRes.Add(r);
                    }
                }
                finally { hPin.Free(); }
            }
            return lstRes;
        }

        /// <summary>Lê n setores; se o bloco falhar, tenta 1 setor só (e devolve n = 1). false se nem esse.</summary>
        private static bool LerComResgate(SptiDiscReader objLeitor, GCHandle hPin, uint lba, ref uint n)
        {
            uint b; byte s, k, a, q; int w;
            if (objLeitor.LerBlocoSpti(hPin.AddrOfPinnedObject(), lba, n, true, out b, out s, out k, out a, out q, out w) && b == n * 2352)
                return true;
            for (int t = 0; t < 3; t++)
                if (objLeitor.LerBlocoSpti(hPin.AddrOfPinnedObject(), lba, 1, true, out b, out s, out k, out a, out q, out w) && b == 2352)
                {
                    n = 1;
                    return true;
                }
            return false;
        }

        /// <summary>O que não se leu, por faixa. Preenchido quando a extração conclui.</summary>
        public TRelatorioExtracao Relatorio { get; private set; }

        public SptiImageExtractor(AcquisitionLogger objLogger)
        {
            m_objLogger = objLogger;
        }

        private bool Interromper(string strMotivo)
        {
            MotivoFalha = strMotivo;
            m_objLogger.RegistrarErro(strMotivo);
            return false;
        }

        /// <summary>A falha é do caminho até o disco, e não do setor?</summary>
        private static bool EhFalhaDeCanal(int intWin32Error, byte bSenseKey)
        {
            if (intWin32Error != 0 && intWin32Error != 87) return true;   // 87: bloco grande demais, tratado à parte
            return bSenseKey == 0x02 || bSenseKey == 0x06;                 // NOT READY, UNIT ATTENTION (disco trocado)
        }

        private static string DescreverFalhaDeCanal(int intWin32Error, byte bSenseKey, byte bAsc, byte bAscq)
        {
            if (intWin32Error == 5)
                return "o Windows recusou o acesso direto à unidade (Win32 5, acesso negado)";
            if (intWin32Error != 0)
                return string.Format("a unidade parou de responder (Win32 {0})", intWin32Error);
            if (bSenseKey == 0x02 && bAsc == 0x3A)
                return "não há disco na unidade (bandeja aberta ou disco retirado)";
            if (bSenseKey == 0x02)
                return string.Format("a unidade não ficou pronta (SCSI 02/{0:X2}/{1:X2})", bAsc, bAscq);
            return "o disco foi trocado ou a unidade foi reiniciada no meio da leitura (SCSI 06)";
        }

        /// <summary>Início (LBA) de cada faixa de áudio, para atribuir um setor à sua faixa.</summary>
        private static List<KeyValuePair<int, uint>> InicioDasFaixas(CdromTocReader.CDROM_TOC objToc)
        {
            List<KeyValuePair<int, uint>> lst = new List<KeyValuePair<int, uint>>();
            int intTotal = objToc.LastTrack - objToc.FirstTrack + 1;
            for (int i = 0; i < intTotal; i++)
            {
                var td = objToc.TrackData[i];
                int intLba = (td.Address_1 * 60 + td.Address_2) * 75 + td.Address_3 - 150;
                lst.Add(new KeyValuePair<int, uint>(td.TrackNumber, (uint)Math.Max(0, intLba)));
            }
            return lst;
        }

        private static int FaixaDoSetor(List<KeyValuePair<int, uint>> lstInicios, uint dwLba)
        {
            int intFaixa = 0;   // antes da faixa 1: área oculta (HTOA)
            foreach (KeyValuePair<int, uint> kv in lstInicios)
                if (kv.Value <= dwLba) intFaixa = kv.Key;
            return intFaixa;
        }

        public bool ExtrairImagem(string strLetraDrive, string strDestinoArquivo, string strPastaDestino, bool blnIsAudio, ProgressoExtracao delProgresso = null, NotificacaoFase delFase = null)
        {
            MotivoFalha = null;
            Relatorio = null;
            TRelatorioExtracao.Apagar(strPastaDestino);
            bool blnOk = ExtrairImagemInterno(strLetraDrive, strDestinoArquivo, strPastaDestino, blnIsAudio, delProgresso, delFase);
            if (!blnOk)
            {
                if (MotivoFalha == null) MotivoFalha = "a extração falhou; o motivo está em aquisicao_detalhada.log";
                // Imagem interrompida não pode ficar para trás com cara de
                // imagem: ninguém deve fatiá-la depois achando que está inteira.
                try { if (File.Exists(strDestinoArquivo)) File.Delete(strDestinoArquivo); } catch { }
                try { string strCue = Path.ChangeExtension(strDestinoArquivo, ".cue"); if (File.Exists(strCue)) File.Delete(strCue); } catch { }
            }
            return blnOk;
        }

        private bool ExtrairImagemInterno(string strLetraDrive, string strDestinoArquivo, string strPastaDestino, bool blnIsAudio, ProgressoExtracao delProgresso, NotificacaoFase delFase)
        {
            m_objLogger.RegistrarInfo(string.Format("Iniciando extração física nativa via Pass Through (SPTI) na unidade {0}:...", strLetraDrive));
            if (delFase != null) delFase("SONDANDO HARDWARE…", string.Format("Abertura de porta SCSI/SPTI na unidade {0}:", strLetraDrive));

            using (SptiDiscReader objLeitor = new SptiDiscReader(m_objLogger))
            {
                if (!objLeitor.AbrirUnidade(strLetraDrive))
                {
                    return Interromper(string.Format("não consegui abrir a unidade {0}: para leitura direta.", strLetraDrive));
                }

                // 1. Identificação do Hardware
                string strVendor, strProduct, strRevision;
                if (objLeitor.ObterInquiryHardware(out strVendor, out strProduct, out strRevision))
                {
                    Fabricante = strVendor;
                    Modelo = strProduct;
                    m_objLogger.RegistrarInfo(string.Format("Unidade Óptica: {0} {1} (Rev: {2})", strVendor, strProduct, strRevision));
                    if (delFase != null) delFase("SONDANDO HARDWARE…", string.Format("Unidade: {0} {1}", strVendor, strProduct));
                }

                // 2. Leitura da TOC
                if (delFase != null) delFase("LENDO TOC DO DISCO…", "Geometria física de faixas do CD");
                CdromTocReader.CDROM_TOC objToc = new CdromTocReader.CDROM_TOC();
                bool blnTemToc = objLeitor.LerToc(out objToc);

                if (!blnTemToc && blnIsAudio)
                {
                    return Interromper("não consegui ler o índice (TOC) do disco. Há um CD de áudio na unidade?");
                }

                // 3. Leitura de CD-TEXT (se for CD-DA)
                string strCdTextAlbum = null;
                string strCdTextArtista = null;
                Dictionary<int, string> dicCdTextTitulos = new Dictionary<int, string>();
                Dictionary<int, string> dicCdTextArtistas = new Dictionary<int, string>();

                if (blnIsAudio)
                {
                    if (delFase != null) delFase("LENDO CD-TEXT…", "Metadados ópticos no Lead-In");
                    if (objLeitor.LerCdText(out strCdTextAlbum, out strCdTextArtista, out dicCdTextTitulos, out dicCdTextArtistas))
                    {
                        m_objLogger.RegistrarInfo(string.Format("CD-TEXT detectado no Lead-In: Álbum='{0}', Artista='{1}'", strCdTextAlbum, strCdTextArtista));
                    }
                    else
                    {
                        m_objLogger.RegistrarDiagnostico("Nenhum CD-TEXT detectado no Lead-In.");
                    }
                }

                // 3.1 Leitura de Subchannel Q (MCN / Código de Barras e ISRCs)
                string strMcn = null;
                Dictionary<int, string> dicIsrcs = new Dictionary<int, string>();
                if (blnIsAudio)
                {
                    if (delFase != null) delFase("LENDO SUBCANAL Q…", "Código de barras (MCN) e ISRCs");
                    if (objLeitor.LerMcn(out strMcn))
                    {
                        m_objLogger.RegistrarInfo(string.Format("Código de Barras (MCN/UPC) detectado no Subchannel Q: {0}", strMcn));
                    }

                    if (blnTemToc)
                    {
                        int intTotalFaixas = objToc.LastTrack - objToc.FirstTrack + 1;
                        for (int i = 0; i < intTotalFaixas; i++)
                        {
                            int intFaixaNum = objToc.TrackData[i].TrackNumber;
                            string strIsrcFaixa;
                            if (objLeitor.LerIsrc(intFaixaNum, out strIsrcFaixa))
                            {
                                dicIsrcs[intFaixaNum] = strIsrcFaixa;
                            }
                        }
                    }
                }

                // 4. Determinação dos limites de LBA
                uint dwSetorSize = blnIsAudio ? 2352u : 2048u;
                uint dwLbaInicio = 0;
                uint dwLbaFim = 0;
                bool blnLeadOutExato = false;   // fim do áudio dito pelo drive (índice completo), e não estimado

                if (blnIsAudio && blnTemToc)
                {
                    int intIndiceLeadOut = objToc.LastTrack - objToc.FirstTrack + 1;
                    int intTotalFaixas = intIndiceLeadOut;

                    // Em CDs multissessão (Enhanced CD / Copy Control), a área de áudio da Sessão 1
                    // termina no LBA onde inicia a primeira faixa de dados ou o lead-out da sessão de áudio.
                    // Tentar ler além disso com comando SCSI READ CD (2352) gera erro 0x05/0x64 (ILLEGAL MODE FOR THIS TRACK).
                    uint dwLbaPrimeiraFaixaDados = 0;
                    int intIndicePrimeiraFaixaDados = -1;
                    int intQtdFaixasAudio = 0;

                    // CD mixed-mode (jogos, CD-ROM com trilha): a faixa de dados vem
                    // ANTES do áudio, no LBA 0. Ela não é música e não se lê como
                    // áudio — lê-la assim eram horas de setores "ilegíveis" virando
                    // zero. O áudio começa na primeira faixa de áudio.
                    int intPrimeiroAudio = -1;
                    for (int i = 0; i < intTotalFaixas; i++)
                    {
                        var td = objToc.TrackData[i];
                        if ((td.Control_Adr & 0x04) == 0) { intPrimeiroAudio = (td.Address_1 * 60 + td.Address_2) * 75 + td.Address_3 - 150; break; }
                    }
                    if (intPrimeiroAudio > 0 && (objToc.TrackData[0].Control_Adr & 0x04) != 0)
                    {
                        dwLbaInicio = (uint)intPrimeiroAudio;
                        m_objLogger.RegistrarInfo(string.Format("CD mixed-mode: a faixa 1 é de dados (LBA 0 a {0}). O áudio começa no LBA {0}; a faixa de dados não é lida.", intPrimeiroAudio));
                    }

                    for (int i = 0; i < intTotalFaixas; i++)
                    {
                        var td = objToc.TrackData[i];
                        bool blnIsAudioTrack = (td.Control_Adr & 0x04) == 0;
                        if (blnIsAudioTrack)
                        {
                            intQtdFaixasAudio++;
                        }
                        else if (dwLbaPrimeiraFaixaDados == 0)
                        {
                            int intStartLba = (td.Address_1 * 60 + td.Address_2) * 75 + td.Address_3 - 150;
                            if (intStartLba > 0 && intStartLba > (int)dwLbaInicio)
                            {
                                dwLbaPrimeiraFaixaDados = (uint)intStartLba;
                                intIndicePrimeiraFaixaDados = i;
                            }
                        }
                    }

                    // Com sessão de dados, o fim do áudio é o lead-out da sessão 1 —
                    // e quem sabe onde ele está é o drive, no índice completo. A
                    // regra de distância abaixo fica só para drive que não responda:
                    // ela confundia uma última faixa real de até 8 s com a "faixa
                    // fantasma" do CDS-300 e a descartava.
                    List<SptiDiscReader.TSessao> lstSessoes = dwLbaPrimeiraFaixaDados > 0 ? objLeitor.LerSessoes() : null;
                    if (lstSessoes != null && lstSessoes.Count >= 2 && lstSessoes[0].intLeadOut > 0)
                    {
                        dwLbaFim = (uint)lstSessoes[0].intLeadOut;
                        blnLeadOutExato = true;
                        int intAudioS1 = lstSessoes[0].lstFaixas.FindAll(f => f[2] == 1 && f[1] < lstSessoes[0].intLeadOut).Count;
                        m_objLogger.RegistrarInfo(string.Format("Disco com {0} sessões: o áudio da sessão 1 ({1} faixa(s)) termina no LBA {2}, dito pelo drive.",
                            lstSessoes.Count, intAudioS1, dwLbaFim));
                        // Faixa marcada como áudio que começa no lead-out ou depois não é
                        // música: é a "faixa fantasma" do CDS-300 (ou já é a sessão 2).
                        for (int i = 0; i < intTotalFaixas; i++)
                        {
                            var td = objToc.TrackData[i];
                            int intIni = (td.Address_1 * 60 + td.Address_2) * 75 + td.Address_3 - 150;
                            if ((td.Control_Adr & 0x04) == 0 && intIni >= (int)dwLbaFim)
                                m_objLogger.RegistrarInfo(string.Format("Faixa {0} (LBA {1}) está depois do fim da sessão de áudio: não é música (faixa fantasma de proteção ou sessão 2).", td.TrackNumber, intIni));
                        }
                    }
                    else if (dwLbaPrimeiraFaixaDados > 0)
                    {
                        dwLbaFim = dwLbaPrimeiraFaixaDados;

                        // Proteção Copy Control (CDS-300 / Macrovision):
                        // Discos protegidos por CDS-300 costumam inserir uma faixa fantasma (marcada na TOC falsamente como áudio)
                        // exatamente no ponto de transição multissessão (Lead-Out da Sessão 1 + Lead-In da Sessão 2, cerca de 11.400 setores).
                        // A fantasma fica no lead-out da sessão 1, a ~11.400 setores dos dados
                        // (lead-out 6.750 + lead-in 4.500 + pré-gap 150). Uma faixa real tem no
                        // mínimo 4 s (300 setores), então começa a 11.700 ou mais: a janela vai
                        // até 11.600 para nunca pegar faixa real. (Antes ia até 12.000 e
                        // descartava a última faixa real quando ela tinha até 8 s.)
                        if (intIndicePrimeiraFaixaDados > 0)
                        {
                            var tdAnterior = objToc.TrackData[intIndicePrimeiraFaixaDados - 1];
                            int intLbaAnterior = (tdAnterior.Address_1 * 60 + tdAnterior.Address_2) * 75 + tdAnterior.Address_3 - 150;
                            if (intLbaAnterior > 0)
                            {
                                long lngGapSessao = (long)dwLbaPrimeiraFaixaDados - intLbaAnterior;
                                if (lngGapSessao >= 11000 && lngGapSessao <= 11600)
                                {
                                    dwLbaFim = (uint)intLbaAnterior;
                                    m_objLogger.RegistrarInfo(string.Format("Proteção Copy Control CDS-300 identificada: Faixa {0} (LBA {1}, gap de {2} setores) é zona de transição multissessão. Delimitando áudio estritamente em {1}.",
                                        tdAnterior.TrackNumber, intLbaAnterior, lngGapSessao));
                                }
                            }
                        }

                        if (dwLbaFim == dwLbaPrimeiraFaixaDados)
                        {
                            m_objLogger.RegistrarInfo(string.Format("CD Multissessão / Enhanced CD detectado: Sessão de áudio delimitada no LBA {0} ({1} faixas de áudio antes da primeira faixa de dados).", dwLbaFim, intQtdFaixasAudio));
                        }
                    }
                    else if (intIndiceLeadOut >= 0 && intIndiceLeadOut < 100)
                    {
                        byte m = objToc.TrackData[intIndiceLeadOut].Address_1;
                        byte s = objToc.TrackData[intIndiceLeadOut].Address_2;
                        byte f = objToc.TrackData[intIndiceLeadOut].Address_3;
                        int intLeadOutLba = (m * 60 + s) * 75 + f - 150;
                        if (intLeadOutLba > 0)
                        {
                            dwLbaFim = (uint)intLeadOutLba;
                        }
                    }
                }

                if (dwLbaFim == 0)
                {
                    uint dwMaxLbaCap, dwBlockSizeCap;
                    if (objLeitor.LerCapacidade(out dwMaxLbaCap, out dwBlockSizeCap))
                    {
                        dwLbaFim = dwMaxLbaCap + 1;
                    }
                }

                if (dwLbaFim == 0)
                {
                    return Interromper("não consegui determinar o tamanho do disco.");
                }

                long lngTamanhoTotalBytes = (long)dwLbaFim * dwSetorSize;
                m_objLogger.RegistrarInfo(string.Format("Mídia detectada: {0} setores ({1:F2} MB).", dwLbaFim, (double)lngTamanhoTotalBytes / (1024 * 1024)));

                // 5. Preparação dos arquivos de destino
                if (File.Exists(strDestinoArquivo)) File.Delete(strDestinoArquivo);
                string strCaminhoCue = Path.ChangeExtension(strDestinoArquivo, ".cue");
                if (blnIsAudio && File.Exists(strCaminhoCue)) File.Delete(strCaminhoCue);

                // 6. Loop de Leitura Resiliente com Buffer Binary Halving
                const uint DW_MAX_SECTORS_INITIAL = 64;
                uint dwMaxSectorsLimit = DW_MAX_SECTORS_INITIAL;
                uint dwCurrentSectors = dwMaxSectorsLimit;
                byte[] arrBuffer = new byte[DW_MAX_SECTORS_INITIAL * dwSetorSize];
                byte[] arrZeroSector = new byte[dwSetorSize];
                GCHandle hPin = GCHandle.Alloc(arrBuffer, GCHandleType.Pinned);

                int intSetoresMascarados = 0;
                int intTotalRetries = 0;
                int intFalhasCanalSeguidas = 0;
                int intMascaradosSeguidos = 0;
                int intSetoresRecuperados = 0;
                List<uint> lstLbasMascarados = new List<uint>();
                uint dwLbaAtual = dwLbaInicio;
                Stopwatch swCronometro = Stopwatch.StartNew();
                long lngUltimoReportMs = 0;

                try
                {
                    using (FileStream fsDestino = new FileStream(strDestinoArquivo, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        // Mixed-mode: a faixa de dados não é lida; o .bin fica com zeros
                        // ali para continuar alinhado ao LBA (o CUE, o corte e o
                        // AccurateRip contam posições a partir do LBA 0).
                        if (dwLbaInicio > 0)
                        {
                            fsDestino.SetLength((long)dwLbaInicio * dwSetorSize);
                            fsDestino.Seek(0, SeekOrigin.End);
                        }
                        while (dwLbaAtual < dwLbaFim)
                        {
                            if (dwLbaAtual + dwCurrentSectors > dwLbaFim)
                            {
                                dwCurrentSectors = dwLbaFim - dwLbaAtual;
                            }

                            uint dwBytesTransferidos;
                            byte bScsiStatus, bSenseKey, bAsc, bAscq;
                            int intWin32Error;

                            bool blnSucessoLeitura = objLeitor.LerBlocoSpti(
                                hPin.AddrOfPinnedObject(),
                                dwLbaAtual,
                                dwCurrentSectors,
                                blnIsAudio,
                                out dwBytesTransferidos,
                                out bScsiStatus,
                                out bSenseKey,
                                out bAsc,
                                out bAscq,
                                out intWin32Error);

                            if (blnSucessoLeitura && dwBytesTransferidos == (dwCurrentSectors * dwSetorSize))
                            {
                                fsDestino.Write(arrBuffer, 0, (int)dwBytesTransferidos);
                                dwLbaAtual += dwCurrentSectors;
                                intFalhasCanalSeguidas = 0;
                                intMascaradosSeguidos = 0;

                                if (swCronometro.ElapsedMilliseconds - lngUltimoReportMs > 1000)
                                {
                                    double dblProgresso = (double)dwLbaAtual / (double)dwLbaFim * 100.0;
                                    double dblSegundos = swCronometro.Elapsed.TotalSeconds;
                                    double dblMbLidos = (double)(dwLbaAtual * dwSetorSize) / (1024 * 1024);
                                    double dblVelocidadeMbS = dblSegundos > 0 ? (dblMbLidos / dblSegundos) : 0.0;

                                    // Só no arquivo de auditoria: na tela, o andamento é do painel STATUS DA OPERAÇÃO.
                                    m_objLogger.RegistrarDiagnostico(string.Format("SPTI: {0:F1}% lido ({1:F1} MB / {2:F1} MB) - {3:F2} MB/s",
                                        dblProgresso, dblMbLidos, (double)lngTamanhoTotalBytes / (1024 * 1024), dblVelocidadeMbS));
                                    if (delProgresso != null)
                                    {
                                        delProgresso(dblProgresso, dblMbLidos, (double)lngTamanhoTotalBytes / (1024 * 1024), dblVelocidadeMbS, dwLbaAtual, dwLbaFim);
                                    }
                                    lngUltimoReportMs = swCronometro.ElapsedMilliseconds;
                                }

                                if (dwCurrentSectors < dwMaxSectorsLimit)
                                {
                                    dwCurrentSectors = dwMaxSectorsLimit;
                                }
                            }
                            else
                            {
                                m_objLogger.RegistrarDiagnostico(string.Format("Falha em LBA {0} (Bloco {1} setores). ScsiStatus: 0x{2:X2}, SenseKey: 0x{3:X2}, ASC: 0x{4:X2}, ASCQ: 0x{5:X2}, Win32: {6}",
                                    dwLbaAtual, dwCurrentSectors, bScsiStatus, bSenseKey, bAsc, bAscq, intWin32Error));

                                // Antes de qualquer resgate: se o caminho até o disco caiu,
                                // esperar e tentar o mesmo bloco; não voltando, parar.
                                if (EhFalhaDeCanal(intWin32Error, bSenseKey))
                                {
                                    string strCanal = DescreverFalhaDeCanal(intWin32Error, bSenseKey, bAsc, bAscq);
                                    if (++intFalhasCanalSeguidas > intTentativasCanal)
                                        return Interromper(string.Format(
                                            "Extração interrompida no setor {0} de {1}: {2}. A imagem incompleta foi descartada; extraia de novo.",
                                            dwLbaAtual, dwLbaFim, strCanal));
                                    m_objLogger.RegistrarAviso(string.Format("LBA {0}: {1}. Tentando de novo em 1 s ({2}/{3})...",
                                        dwLbaAtual, strCanal, intFalhasCanalSeguidas, intTentativasCanal));
                                    if (delFase != null) delFase("AGUARDANDO A UNIDADE…", strCanal);
                                    System.Threading.Thread.Sleep(intEsperaCanalMs);
                                    continue;
                                }

                                if (intWin32Error == 87 && dwCurrentSectors > 1)
                                {
                                    dwMaxSectorsLimit = dwCurrentSectors / 2;
                                    if (dwMaxSectorsLimit == 0) dwMaxSectorsLimit = 1;
                                    dwCurrentSectors = dwMaxSectorsLimit;
                                    m_objLogger.RegistrarDiagnostico(string.Format("[HBA Hardware] Controlador rejeitou bloco. Redefinindo teto para {0} setores ({1} KB)...", dwMaxSectorsLimit, (dwMaxSectorsLimit * dwSetorSize) / 1024));
                                    continue;
                                }

                                if (dwCurrentSectors > 1)
                                {
                                    uint dwAnterior = dwCurrentSectors;
                                    dwCurrentSectors /= 2;
                                    if (dwCurrentSectors == 0) dwCurrentSectors = 1;
                                    m_objLogger.RegistrarDiagnostico(string.Format("[Buffer Halving] LBA {0}: Reduzindo buffer de {1} para {2} setores...", dwLbaAtual, dwAnterior, dwCurrentSectors));
                                    continue;
                                }

                                // Se em bloco unitário (1 setor) o hardware óptico retornar SenseKey 0x05 e ASC 0x64,
                                // a controladora está declarando que o comando de leitura de áudio (2352) é ilegal nesta área.
                                // Isso sinaliza o término definitivo da sessão de áudio Red Book (início do gap multissessão / dados).
                                if (blnIsAudio && bSenseKey == 0x05 && bAsc == 0x64)
                                {
                                    m_objLogger.RegistrarInfo(string.Format("Fim físico da sessão de áudio identificado no LBA {0} (SCSI 0x05/0x64: ILLEGAL MODE FOR THIS TRACK). Concluindo aquisição de áudio.", dwLbaAtual));
                                    dwLbaFim = dwLbaAtual;
                                    break;
                                }

                                m_objLogger.RegistrarAviso(string.Format("Setor problemático isolado em LBA {0}. Iniciando rotina de resgate com descarte de cache...", dwLbaAtual));

                                bool blnRecuperouSetor = false;
                                for (int intTentativa = 1; intTentativa <= 3; intTentativa++)
                                {
                                    intTotalRetries++;
                                    m_objLogger.RegistrarDiagnostico(string.Format("Tentativa {0}/3 para LBA {1}: Purgando cache óptico...", intTentativa, dwLbaAtual));
                                    objLeitor.DescartarCacheDrive(dwLbaAtual, dwLbaFim);
                                    System.Threading.Thread.Sleep(200);

                                    blnSucessoLeitura = objLeitor.LerBlocoSpti(
                                        hPin.AddrOfPinnedObject(),
                                        dwLbaAtual,
                                        1,
                                        blnIsAudio,
                                        out dwBytesTransferidos,
                                        out bScsiStatus,
                                        out bSenseKey,
                                        out bAsc,
                                        out bAscq,
                                        out intWin32Error);

                                    if (blnSucessoLeitura && dwBytesTransferidos == dwSetorSize)
                                    {
                                        m_objLogger.RegistrarInfo(string.Format("Sucesso! LBA {0} recuperado após descarte de cache na tentativa {1}.", dwLbaAtual, intTentativa));
                                        fsDestino.Write(arrBuffer, 0, (int)dwSetorSize);
                                        dwLbaAtual++;
                                        blnRecuperouSetor = true;
                                        intSetoresRecuperados++;
                                        intMascaradosSeguidos = 0;
                                        break;
                                    }
                                    if (EhFalhaDeCanal(intWin32Error, bSenseKey)) break;
                                }

                                // O canal caiu durante o resgate: o setor não é culpado.
                                // Volta ao laço, que espera a unidade ou interrompe.
                                // Conta como tentativa de canal, para não girar para sempre
                                // num drive que alterna entre setor ruim e "não pronto".
                                if (!blnRecuperouSetor && EhFalhaDeCanal(intWin32Error, bSenseKey))
                                {
                                    string strCanal = DescreverFalhaDeCanal(intWin32Error, bSenseKey, bAsc, bAscq);
                                    if (++intFalhasCanalSeguidas > intTentativasCanal)
                                        return Interromper(string.Format(
                                            "Extração interrompida no setor {0} de {1}: {2}. A imagem incompleta foi descartada; extraia de novo.",
                                            dwLbaAtual, dwLbaFim, strCanal));
                                    System.Threading.Thread.Sleep(intEsperaCanalMs);
                                    dwCurrentSectors = 1;
                                    continue;
                                }

                                if (!blnRecuperouSetor)
                                {
                                    intSetoresMascarados++;
                                    lstLbasMascarados.Add(dwLbaAtual);
                                    m_objLogger.RegistrarAviso(string.Format("LBA {0} irrecuperável. Mascarando com {1} bytes de silêncio para salvar imagem.", dwLbaAtual, dwSetorSize));
                                    fsDestino.Write(arrZeroSector, 0, (int)dwSetorSize);
                                    dwLbaAtual++;
                                    if (++intMascaradosSeguidos >= intLimiteMascaradosSeguidos)
                                        return Interromper(string.Format(
                                            "Extração interrompida: {0} setores seguidos ilegíveis ({1} s de áudio) até o LBA {2}. " +
                                            "O disco está ilegível nesta região; limpe-o ou tente outra unidade. A imagem incompleta foi descartada.",
                                            intMascaradosSeguidos, intMascaradosSeguidos / TRelatorioExtracao.SetoresPorSegundo, dwLbaAtual));
                                }

                                dwCurrentSectors = dwMaxSectorsLimit;
                            }
                        }
                    }
                }
                finally
                {
                    hPin.Free();
                }

                swCronometro.Stop();
                m_objLogger.RegistrarInfo(string.Format("Extração física concluída em {0:F1}s! Setores: {1}, Mascarados: {2}, Retries: {3}",
                    swCronometro.Elapsed.TotalSeconds, dwLbaFim, intSetoresMascarados, intTotalRetries));
                if (delProgresso != null)
                {
                    delProgresso(100.0, (double)lngTamanhoTotalBytes / (1024 * 1024), (double)lngTamanhoTotalBytes / (1024 * 1024), 0.0, dwLbaFim, dwLbaFim);
                }

                // O que não se leu sai daqui para a área de trabalho, por faixa:
                // a aprovação e o ledger precisam saber, mesmo com o ARCA reaberto.
                TRelatorioExtracao objRelatorio = new TRelatorioExtracao
                {
                    intSetoresMascarados = intSetoresMascarados,
                    intSetoresRecuperados = intSetoresRecuperados
                };
                if (lstLbasMascarados.Count > 0)
                {
                    List<KeyValuePair<int, uint>> lstInicios = blnTemToc ? InicioDasFaixas(objToc) : new List<KeyValuePair<int, uint>>();
                    foreach (uint dwLba in lstLbasMascarados)
                    {
                        int intFaixa = FaixaDoSetor(lstInicios, dwLba);
                        int intQtd;
                        objRelatorio.dicMascaradosPorFaixa.TryGetValue(intFaixa, out intQtd);
                        objRelatorio.dicMascaradosPorFaixa[intFaixa] = intQtd + 1;
                    }
                    m_objLogger.RegistrarAviso(string.Format("EXTRAÇÃO NÃO EXATA: {0} setor(es) ilegível(is) substituído(s) por silêncio — {1}.",
                        intSetoresMascarados, objRelatorio.ResumoPorFaixa()));
                }
                try { objRelatorio.Gravar(strPastaDestino); }
                catch (Exception exRel) { m_objLogger.RegistrarAviso("Não consegui gravar " + TRelatorioExtracao.NomeArquivo + ": " + exRel.Message); }
                Relatorio = objRelatorio;

                // 7. Geração do Arquivo CUE
                if (blnIsAudio && blnTemToc)
                {
                    if (delFase != null) delFase("GERANDO ARQUIVO CUE…", "Gravando índice de faixas e metadados no CUE");
                    GerarArquivoCue(strCaminhoCue, strDestinoArquivo, objToc, strCdTextAlbum, strCdTextArtista, dicCdTextTitulos, dicCdTextArtistas, strMcn, dicIsrcs, dwLbaFim, blnLeadOutExato);
                }

                return true;
            }
        }

        private void GerarArquivoCue(
            string strCaminhoCue,
            string strCaminhoBin,
            CdromTocReader.CDROM_TOC objToc,
            string strAlbumCdText,
            string strArtistaCdText,
            Dictionary<int, string> dicTitulos,
            Dictionary<int, string> dicArtistas,
            string strMcn,
            Dictionary<int, string> dicIsrcs,
            uint dwLbaFimAudio,
            bool blnLeadOutExato = false)
        {
            m_objLogger.RegistrarDiagnostico(string.Format("Gerando arquivo CUE Sheet em: {0}", strCaminhoCue));

            StringBuilder sbCue = new StringBuilder();
            sbCue.AppendLine("REM SESSION 1");

            if (!string.IsNullOrEmpty(strMcn))
            {
                sbCue.AppendFormat("CATALOG {0}\r\n", strMcn);
            }

            if (!string.IsNullOrEmpty(strAlbumCdText))
            {
                sbCue.AppendFormat("TITLE \"{0}\"\r\n", strAlbumCdText.Replace("\"", ""));
            }
            if (!string.IsNullOrEmpty(strArtistaCdText))
            {
                sbCue.AppendFormat("PERFORMER \"{0}\"\r\n", strArtistaCdText.Replace("\"", ""));
            }

            string strNomeArquivoBin = Path.GetFileName(strCaminhoBin);
            sbCue.AppendFormat("FILE \"{0}\" BINARY\r\n", strNomeArquivoBin);

            int intTotalFaixas = objToc.LastTrack - objToc.FirstTrack + 1;
            int intSessaoAtual = 1;
            int intPrimeiroAudioCue = int.MaxValue;
            for (int i = 0; i < intTotalFaixas; i++)
            {
                var tdA = objToc.TrackData[i];
                if ((tdA.Control_Adr & 0x04) == 0) { intPrimeiroAudioCue = Math.Max(0, (tdA.Address_1 * 60 + tdA.Address_2) * 75 + tdA.Address_3 - 150); break; }
            }
            bool blnLeadOutSessao1Inserido = false;

            for (int i = 0; i < intTotalFaixas; i++)
            {
                var td = objToc.TrackData[i];
                int intTrackNum = td.TrackNumber;
                bool blnIsAudioTrack = (td.Control_Adr & 0x04) == 0;

                int intStartLba = (td.Address_1 * 60 + td.Address_2) * 75 + td.Address_3 - 150;
                if (intStartLba < 0) intStartLba = 0;

                int intMin = (intStartLba / 75) / 60;
                int intSec = (intStartLba / 75) % 60;
                int intFrame = intStartLba % 75;

                // Em CDs multissessão / Copy Control, qualquer faixa que inicie a partir de dwLbaFimAudio
                // pertence à Sessão 2 ou à zona de transição de dados.
                // Só dados DEPOIS do áudio abrem a sessão 2 (CD Extra). Dados antes
                // do áudio (mixed-mode) são a faixa 1 da própria sessão 1.
                bool blnDadosAntesDoAudio = !blnIsAudioTrack && intStartLba < intPrimeiroAudioCue;
                if ((!blnIsAudioTrack || (dwLbaFimAudio > 0 && intStartLba >= dwLbaFimAudio)) && !blnDadosAntesDoAudio && intSessaoAtual == 1)
                {
                    if (!blnLeadOutSessao1Inserido)
                    {
                        int intLoMin = ((int)dwLbaFimAudio / 75) / 60;
                        int intLoSec = ((int)dwLbaFimAudio / 75) % 60;
                        int intLoFrame = (int)dwLbaFimAudio % 75;
                        sbCue.AppendFormat("REM LEAD-OUT {0:D2}:{1:D2}:{2:D2}\r\n", intLoMin, intLoSec, intLoFrame);
                        // O lead-out acima veio do drive (índice completo), não de estimativa:
                        // quem lê o CUE pode confiar nele como fim exato do áudio.
                        if (blnLeadOutExato) sbCue.Append("REM ARCA-LEADOUT-EXATO\r\n");
                        blnLeadOutSessao1Inserido = true;
                    }
                    intSessaoAtual = 2;
                    sbCue.AppendFormat("REM SESSION {0}\r\n", intSessaoAtual);
                }

                // Faixas da Sessão 2 ou além do limite de áudio são marcadas como MODE2/2352 para não serem tratadas como áudio
                string strTipoFaixa = blnDadosAntesDoAudio ? "MODE1/2352"
                    : (blnIsAudioTrack && (dwLbaFimAudio == 0 || intStartLba < dwLbaFimAudio)) ? "AUDIO" : "MODE2/2352";
                sbCue.AppendFormat("  TRACK {0:D2} {1}\r\n", intTrackNum, strTipoFaixa);

                if (dicTitulos != null && dicTitulos.ContainsKey(intTrackNum) && !string.IsNullOrEmpty(dicTitulos[intTrackNum]))
                {
                    sbCue.AppendFormat("    TITLE \"{0}\"\r\n", dicTitulos[intTrackNum].Replace("\"", ""));
                }
                if (dicArtistas != null && dicArtistas.ContainsKey(intTrackNum) && !string.IsNullOrEmpty(dicArtistas[intTrackNum]))
                {
                    sbCue.AppendFormat("    PERFORMER \"{0}\"\r\n", dicArtistas[intTrackNum].Replace("\"", ""));
                }
                if (dicIsrcs != null && dicIsrcs.ContainsKey(intTrackNum) && !string.IsNullOrEmpty(dicIsrcs[intTrackNum]))
                {
                    sbCue.AppendFormat("    ISRC {0}\r\n", dicIsrcs[intTrackNum]);
                }

                sbCue.AppendFormat("    INDEX 01 {0:D2}:{1:D2}:{2:D2}\r\n", intMin, intSec, intFrame);
            }

            int intIndiceLeadOut = intTotalFaixas;
            if (intIndiceLeadOut >= 0 && intIndiceLeadOut < 100)
            {
                var tdLo = objToc.TrackData[intIndiceLeadOut];
                int intLoLba = (tdLo.Address_1 * 60 + tdLo.Address_2) * 75 + tdLo.Address_3 - 150;
                if (intLoLba < 0) intLoLba = 0;
                int intLoMin = (intLoLba / 75) / 60;
                int intLoSec = (intLoLba / 75) % 60;
                int intLoFrame = intLoLba % 75;

                if (!blnLeadOutSessao1Inserido)
                {
                    int intRealLoLba = (dwLbaFimAudio > 0) ? (int)dwLbaFimAudio : intLoLba;
                    int intRealMin = (intRealLoLba / 75) / 60;
                    int intRealSec = (intRealLoLba / 75) % 60;
                    int intRealFrame = intRealLoLba % 75;
                    sbCue.AppendFormat("REM LEAD-OUT {0:D2}:{1:D2}:{2:D2}\r\n", intRealMin, intRealSec, intRealFrame);
                }
                else
                {
                    sbCue.AppendFormat("REM LEAD-OUT-SESSION2 {0:D2}:{1:D2}:{2:D2}\r\n", intLoMin, intLoSec, intLoFrame);
                }
            }

            File.WriteAllText(strCaminhoCue, sbCue.ToString(), Encoding.UTF8);
            m_objLogger.RegistrarInfo("Arquivo CUE Sheet gerado com sucesso em UTF-8.");
        }
    }
}
