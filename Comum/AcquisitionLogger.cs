using System;
using System.IO;
using System.Text;

namespace ARCA.Modelo
{
    /// <summary>
    /// Gerenciador de logs diagnósticos ricos para o processo de aquisição física.
    /// Registra detalhes em tempo real no arquivo 'aquisicao_detalhada.log' para auditoria
    /// e repassa resumos para o delegate de interface.
    /// </summary>
    public class AcquisitionLogger
    {
        private readonly string m_strCaminhoArquivoLog;
        private readonly Action<string> m_actCallbackUi;
        private readonly object m_objLock = new object();

        public AcquisitionLogger(string strPastaDestino, Action<string> actCallbackUi)
        {
            m_actCallbackUi = actCallbackUi;
            try
            {
                if (!Directory.Exists(strPastaDestino))
                {
                    Directory.CreateDirectory(strPastaDestino);
                }
                m_strCaminhoArquivoLog = Path.Combine(strPastaDestino, "aquisicao_detalhada.log");

                lock (m_objLock)
                {
                    StringBuilder sbCabecalho = new StringBuilder();
                    sbCabecalho.AppendLine();
                    sbCabecalho.AppendLine("================================================================================");
                    sbCabecalho.AppendFormat("SESSÃO DE AQUISIÇÃO FÍSICA INICIADA EM: {0:yyyy-MM-dd HH:mm:ss.fff}\r\n", DateTime.Now);
                    sbCabecalho.AppendLine("================================================================================");
                    File.AppendAllText(m_strCaminhoArquivoLog, sbCabecalho.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // O lugar do log é a área de trabalho, sempre. Antes, uma falha
                // aqui (pasta momentaneamente inacessível) mandava o log da sessão
                // inteira para a pasta do programa. Agora o cabeçalho fica pendente
                // e sai junto com a próxima linha que conseguir ser gravada.
                m_strCaminhoArquivoLog = Path.Combine(strPastaDestino ?? "", "aquisicao_detalhada.log");
                m_sbPendente.AppendLine();
                m_sbPendente.AppendFormat("SESSÃO DE AQUISIÇÃO FÍSICA INICIADA EM: {0:yyyy-MM-dd HH:mm:ss.fff}\r\n", DateTime.Now);
            }
        }

        // Linhas que não puderam ser gravadas ainda (arquivo ocupado, pasta fora
        // do ar por um instante). Saem antes da próxima gravação bem-sucedida.
        private readonly StringBuilder m_sbPendente = new StringBuilder();

        public string CaminhoArquivoLog
        {
            get { return m_strCaminhoArquivoLog; }
        }

        public void RegistrarInfo(string strMensagem)
        {
            GravarLogArquivo("[INFO] " + strMensagem);
            if (m_actCallbackUi != null)
            {
                m_actCallbackUi(strMensagem);
            }
        }

        public void RegistrarDiagnostico(string strMensagem)
        {
            GravarLogArquivo("[DIAG] " + strMensagem);
        }

        public void RegistrarAviso(string strMensagem)
        {
            GravarLogArquivo("[AVISO] " + strMensagem);
            if (m_actCallbackUi != null)
            {
                m_actCallbackUi("⚠ " + strMensagem);
            }
        }

        public void RegistrarErro(string strMensagem, Exception ex = null)
        {
            string strTexto = "[ERRO] " + strMensagem;
            if (ex != null)
            {
                strTexto += string.Format("\r\nDetalhes da Exceção: {0}\r\nStack Trace:\r\n{1}", ex.Message, ex.StackTrace);
            }

            GravarLogArquivo(strTexto);
            if (m_actCallbackUi != null)
            {
                m_actCallbackUi("❌ " + strMensagem);
            }
        }

        public void RegistrarProgressoLba(long lngLbaAtual, long lngTotalLba, int intTentativas, int intSetoresBuffer)
        {
            if (lngTotalLba <= 0) return;
            double dblPct = (double)lngLbaAtual / lngTotalLba * 100.0;
            string strLog = string.Format("LBA {0}/{1} ({2:F1}%) | Bloco: {3} setores | Retries: {4}",
                lngLbaAtual, lngTotalLba, dblPct, intSetoresBuffer, intTentativas);

            GravarLogArquivo("[PROGRESSO] " + strLog);
        }

        public void RegistrarFalhaSetor(long lngLba, string strMotivo, bool blnRecuperado)
        {
            string strStatus = blnRecuperado ? "RECUPERADO VIA BINARY HALVING" : "ZERO-MASKED (SUBSTITUÍDO POR SILÊNCIO)";
            string strLog = string.Format("SETOR {0} | MOTIVO: {1} | STATUS: {2}", lngLba, strMotivo, strStatus);
            GravarLogArquivo("[SETOR_CRÍTICO] " + strLog);

            if (m_actCallbackUi != null)
            {
                m_actCallbackUi(string.Format("Setor {0}: {1}", lngLba, strStatus));
            }
        }

        private void GravarLogArquivo(string strLinha)
        {
            lock (m_objLock)
            {
                m_sbPendente.AppendFormat("[{0:HH:mm:ss.fff}] {1}\r\n", DateTime.Now, strLinha);
                // Três tentativas curtas; não dando, a linha espera a próxima vez.
                for (int intTentativa = 0; intTentativa < 3; intTentativa++)
                {
                    try
                    {
                        string strDir = Path.GetDirectoryName(m_strCaminhoArquivoLog);
                        if (!string.IsNullOrEmpty(strDir) && !Directory.Exists(strDir)) Directory.CreateDirectory(strDir);
                        File.AppendAllText(m_strCaminhoArquivoLog, m_sbPendente.ToString(), Encoding.UTF8);
                        m_sbPendente.Length = 0;
                        return;
                    }
                    catch { System.Threading.Thread.Sleep(50); }
                }
            }
        }
    }
}
