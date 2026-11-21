using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ARCA.Interop;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>
    /// A cópia segura de um CD, do leitor até os WMA na pasta de entrada (fora do acervo).
    /// Sem janela: a JanelaRipar mostra o andamento; os testes podem chamar direto.
    ///
    ///   1. lê o disco inteiro pelo SCSI (SPTI), setor a setor, com resgate dos setores
    ///      difíceis — sai uma imagem AudioCD.bin e o índice AudioCD.cue (com o CD-TEXT);
    ///   2. confere no AccurateRip (pelo ARCA Central) e relê o que a base não confirmar;
    ///   3. corta as faixas já com o offset do leitor corrigido e codifica em WMA Lossless,
    ///      conferindo cada WMA decodificado contra o trecho do disco (SHA-256);
    ///   4. grava em cada faixa o índice do CD (WM/MCDI), o leitor e o veredito.
    ///
    /// A imagem (≈ 700 MB) é apagada no fim; o relatório e o log ficam na pasta.
    /// Nada vai para o acervo aqui: isso é o "Aplicar" do Identificar, com a autorização do usuário.
    /// </summary>
    public sealed class TRipador
    {
        /// <summary>Folga de disco exigida antes de começar: a imagem de um CD cheio (≈ 830 MB) mais os WMA (≈ 60%).</summary>
        public const long BytesNecessarios = 1600L * 1024 * 1024;

        public readonly char chrLetra;
        public readonly string strPasta;

        public Action<string> objLog;
        public Action<string, string> objFase;
        /// <summary>Etapa (LEITURA, RELEITURA, CODIFICAÇÃO), porcentagem da etapa, detalhe.</summary>
        public Action<string, double, string> objProgresso;
        /// <summary>Etapa concluída: 1 leitura, 2 AccurateRip e releitura, 3 WMA, 4 etiquetas e relatório.</summary>
        public Action<int> objEtapa;

        public string strFabricante, strModelo;
        public string strAlbumCdText, strArtistaCdText;
        public int intOffset;
        public TRelatorioExtracao objRelatorio;
        public readonly List<TConferenciaWma> lstConferencia = new List<TConferenciaWma>();
        public List<string> lstWma = new List<string>();
        public string strFalha;

        public TRipador(char chrLetra, string strPasta)
        {
            this.chrLetra = char.ToUpperInvariant(chrLetra);
            this.strPasta = strPasta;
        }

        public string Bin { get { return Path.Combine(strPasta, "AudioCD.bin"); } }
        public string Cue { get { return Path.Combine(strPasta, "AudioCD.cue"); } }

        /// <summary>Uma pasta nova na entrada para este disco ("CD de 2026-11-21 10.42").</summary>
        public static string PastaNova(string strEntrada)
        {
            string strBase = Path.Combine(strEntrada, "CD de " + DateTime.Now.ToString("yyyy-MM-dd HH.mm"));
            string s = strBase;
            for (int i = 2; Directory.Exists(s); i++) s = strBase + " (" + i + ")";
            return s;
        }

        /// <summary>As faixas como o índice do disco as dá (depois da leitura).</summary>
        public List<TrackInfo> lstFaixasCue = new List<TrackInfo>();

        private void Etapa(int n) { if (objEtapa != null) objEtapa(n); }
        private AcquisitionLogger m_objLogger;

        private void Log(string s)
        {
            if (objLog != null) objLog(s);
            if (m_objLogger != null) { try { m_objLogger.RegistrarInfo(s); } catch { } }
        }
        private void Fase(string a, string b) { if (objFase != null) objFase(a, b); }
        private void Progresso(string a, double p, string d) { if (objProgresso != null) objProgresso(a, p, d); }

        /// <summary>Tudo, do leitor aos WMA conferidos. false com strFalha se não deu.</summary>
        public bool Ripar()
        {
            try
            {
                Directory.CreateDirectory(strPasta);
                long lngLivre = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(strPasta))).AvailableFreeSpace;
                if (lngLivre < BytesNecessarios)
                    return Falhar(string.Format("só há {0:N0} MB livres em {1}; a cópia precisa de {2:N0} MB.",
                                                lngLivre / 1048576, Path.GetPathRoot(strPasta), BytesNecessarios / 1048576));

                AcquisitionLogger objLogger = new AcquisitionLogger(strPasta, null);
                m_objLogger = objLogger;   // daqui em diante o registro da janela também vai para o log da pasta
                SptiImageExtractor objExtrator = new SptiImageExtractor(objLogger);

                // ---- 1. leitura ----
                Fase("LENDO O DISCO", string.Format("Leitura direta do leitor {0}: pelo SCSI, setor a setor", chrLetra));
                DateTime dtInicio = DateTime.Now;
                bool blnLeu = objExtrator.ExtrairImagem(chrLetra.ToString(), Bin, strPasta, true,
                    delegate(double dblPct, double dblMb, double dblTotal, double dblVel, long lngLba, long lngTotal)
                    {
                        Progresso("LEITURA", dblPct, string.Format("{0:F0} de {1:F0} MB · {2:F1} MB/s", dblMb, dblTotal, dblVel));
                    },
                    delegate(string strVerbo, string strDetalhe) { Fase(strVerbo, strDetalhe); });
                strFabricante = objExtrator.Fabricante; strModelo = objExtrator.Modelo;
                if (!blnLeu) return Falhar(objExtrator.MotivoFalha ?? "a leitura do disco falhou");
                objRelatorio = objExtrator.Relatorio ?? new TRelatorioExtracao();
                Log(string.Format("Disco lido em {0:F0} s ({1}).", (DateTime.Now - dtInicio).TotalSeconds,
                                  objRelatorio.Exata ? "todos os setores lidos" : objRelatorio.intSetoresMascarados + " setor(es) ilegível(is) viraram silêncio: " + objRelatorio.ResumoPorFaixa()));
                lstFaixasCue = ProcessadorExtracaoAudio.ParsearCue(Cue, out strAlbumCdText, out strArtistaCdText);
                Etapa(1);
                if (!string.IsNullOrEmpty(strAlbumCdText) || !string.IsNullOrEmpty(strArtistaCdText))
                    Log(string.Format("CD-TEXT: {0} — {1}", strArtistaCdText, strAlbumCdText));

                // ---- 2. AccurateRip e releitura ----
                intOffset = ConferenciaBitPerfect.Preparar(objExtrator, chrLetra, strPasta, objRelatorio, Log, Fase,
                    delegate(string strTitulo, double dblPct, string strDetalhe) { Progresso("RELEITURA", dblPct, strDetalhe); });

                Etapa(2);

                // ---- 3. corte e WMA Lossless ----
                Fase("CODIFICANDO", "WMA Lossless, cada faixa conferida contra o disco");
                bool blnCortou = ProcessadorExtracaoAudio.FatiarEConverterParaWma(Cue, NuloSeVazio(strArtistaCdText), NuloSeVazio(strAlbumCdText),
                    null, null, null, delegate(string m) { Log(m); },
                    delegate(int intFaixa, int intTotal, double dblPct)
                    {
                        double dblGeral = intTotal > 0 ? ((intFaixa - 1) + dblPct / 100.0) / intTotal * 100.0 : dblPct;
                        Progresso("CODIFICAÇÃO", dblGeral, string.Format("faixa {0} de {1}", intFaixa, intTotal));
                    },
                    intOffset, lstConferencia);
                if (!blnCortou) return Falhar("o corte das faixas em WMA falhou (detalhes no registro)");

                Etapa(3);

                // ---- 4. etiquetas e relatório ----
                ConferenciaBitPerfect.Concluir(strPasta, objRelatorio, lstConferencia, Log);
                lstWma = lstConferencia.Where(c => !string.IsNullOrEmpty(c.strArquivoWma) && File.Exists(c.strArquivoWma))
                                       .OrderBy(c => c.intFaixa).Select(c => c.strArquivoWma).ToList();

                // A imagem já cumpriu o papel: cada WMA foi conferido contra ela.
                try { File.Delete(Bin); } catch (Exception ex) { Log("Aviso: não apaguei a imagem " + Bin + ": " + ex.Message); }
                Etapa(4);
                Programa.Registrar("rip concluído: " + strPasta + " · " + lstWma.Count + " faixa(s) · " + objRelatorio.ResumoBitPerfect());
                return lstWma.Count > 0 || Falhar("nenhuma faixa saiu");
            }
            catch (Exception ex)
            {
                Programa.Registrar("rip interrompido: " + ex);
                return Falhar(ex.Message);
            }
        }

        private bool Falhar(string s) { strFalha = s; Log("FALHOU: " + s); Programa.Registrar("rip falhou: " + s); return false; }

        private static string NuloSeVazio(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        /// <summary>Todas as faixas bit-perfect (AccurateRip ou duas leituras idênticas) e nenhum setor perdido?</summary>
        public bool Exata
        {
            get
            {
                return objRelatorio != null && objRelatorio.Exata && objRelatorio.dicVerificacao.Count > 0
                    && objRelatorio.dicVerificacao.Values.All(v => v.Nivel != NivelBitPerfect.NaoComprovada);
            }
        }
    }
}
