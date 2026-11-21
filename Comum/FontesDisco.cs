using System;
using System.Collections.Generic;
using System.IO;
using ARCA.Interop;

namespace ARCA.Modelo
{
    /// <summary>
    /// O AudioCD.bin da extração como disco: setores de 2352 bytes desde o
    /// LBA 0, exatamente como o drive os entregou (sem correção de offset).
    /// </summary>
    public sealed class TFonteBin : IFonteDisco, IDisposable
    {
        private readonly string m_strArquivo;
        private FileStream m_objArq;
        private byte[] m_arrBytes = new byte[0];

        public TFonteBin(string strArquivo)
        {
            m_strArquivo = strArquivo;
            TotalAmostras = new FileInfo(strArquivo).Length / 4;
        }

        public int LbaInicial { get { return 0; } }
        public long TotalAmostras { get; private set; }

        public void Reiniciar()
        {
            Dispose();
            m_objArq = new FileStream(m_strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        }

        public int Ler(uint[] arrDestino, int intMax)
        {
            if (m_arrBytes.Length < intMax * 4) m_arrBytes = new byte[intMax * 4];
            int intLidos = 0;
            while (intLidos < intMax * 4)
            {
                int r = m_objArq.Read(m_arrBytes, intLidos, intMax * 4 - intLidos);
                if (r <= 0) break;
                intLidos += r;
            }
            int intAmostras = intLidos / 4;
            Buffer.BlockCopy(m_arrBytes, 0, arrDestino, 0, intAmostras * 4);
            return intAmostras;
        }

        public void Dispose()
        {
            if (m_objArq != null) { m_objArq.Dispose(); m_objArq = null; }
        }
    }

    /// <summary>
    /// As faixas WMA de um álbum, decodificadas em sequência como se fossem o
    /// disco: a posição 0 é o início da primeira faixa. Só serve para áudio de
    /// CD (16 bits, estéreo, 44,1 kHz) — outro formato não tem assinatura no
    /// AccurateRip, e a abertura recusa.
    ///
    /// A conferência só faz sentido se cada arquivo tiver exatamente o
    /// tamanho que o índice do CD dá à faixa: é o que Validar confere antes.
    /// </summary>
    public sealed class TFonteWma : IFonteDisco, IDisposable
    {
        private readonly List<string> m_lstArquivos;
        private int m_intAtual;
        private LeitorAudio m_objLeitor;
        private byte[] m_arrResto = new byte[0];
        private int m_intRestoPos;

        public TFonteWma(List<string> lstArquivos, int intLbaInicial)
        {
            m_lstArquivos = lstArquivos;
            LbaInicial = intLbaInicial;
        }

        public int LbaInicial { get; private set; }
        public long TotalAmostras { get; private set; }

        /// <summary>Algo que não impede a conferência mas deve ser dito, ou null.</summary>
        public string Observacao { get; private set; }

        /// <summary>
        /// Decodifica cada arquivo uma vez para contar as amostras e confere
        /// com o índice. null se tudo bate; senão, o porquê.
        /// </summary>
        public string Validar(TTocAudio objToc, Func<bool> cancelou)
        {
            if (m_lstArquivos.Count != objToc.QtdFaixas)
                return string.Format("o álbum tem {0} arquivo(s) e o índice do CD tem {1} faixa(s) de áudio", m_lstArquivos.Count, objToc.QtdFaixas);
            TotalAmostras = 0;
            for (int i = 0; i < m_lstArquivos.Count; i++)
            {
                using (LeitorAudio l = new LeitorAudio())
                {
                    if (!l.Abrir(m_lstArquivos[i])) return Path.GetFileName(m_lstArquivos[i]) + ": " + l.Falha;
                    if (l.Canais != 2 || l.Bits != 16 || l.Taxa != 44100)
                        return string.Format("{0} não é áudio de CD ({1} canais, {2} bits, {3} Hz)", Path.GetFileName(m_lstArquivos[i]), l.Canais, l.Bits, l.Taxa);
                    long n = 0;
                    byte[] b;
                    while ((b = l.ProximoBlocoBytes()) != null)
                    {
                        n += b.Length / 4;
                        if (cancelou != null && cancelou()) throw new OperationCanceledException();
                    }
                    if (!l.Terminou) return Path.GetFileName(m_lstArquivos[i]) + ": " + (l.Falha ?? "a decodificação parou antes do fim");
                    long esperado = objToc.AmostrasDaFaixa(i);
                    // A última faixa pode vir curta: o WMP não lê o último
                    // setor do disco. Até 5 setores não importa — o
                    // AccurateRip ignora exatamente as 5 últimas setores.
                    bool blnUltima = i == m_lstArquivos.Count - 1;
                    if (blnUltima && n < esperado && esperado - n <= AccurateRip.AmostrasPontas)
                        Observacao = string.Format("a última faixa tem {0} setor(es) a menos que o CD (o fim do disco não foi lido); a conferência ignora essa ponta",
                                                   (esperado - n) / TTocAudio.AmostrasPorSetor);
                    else if (n != esperado)
                        return string.Format("{0} tem {1} amostras, e o índice do CD dá {2} à faixa ({3:+#;-#;0}): o arquivo não foi cortado nas fronteiras do CD",
                                             Path.GetFileName(m_lstArquivos[i]), n, esperado, n - esperado);
                    TotalAmostras += n;
                }
            }
            return null;
        }

        public void Reiniciar()
        {
            Fechar();
            m_intAtual = -1;
            m_arrResto = new byte[0];
            m_intRestoPos = 0;
        }

        public int Ler(uint[] arrDestino, int intMax)
        {
            int intFeitos = 0;
            while (intFeitos < intMax)
            {
                if (m_intRestoPos >= m_arrResto.Length)
                {
                    m_arrResto = ProximoBloco();
                    m_intRestoPos = 0;
                    if (m_arrResto == null) { m_arrResto = new byte[0]; break; }
                }
                int intQtd = Math.Min(intMax - intFeitos, (m_arrResto.Length - m_intRestoPos) / 4);
                Buffer.BlockCopy(m_arrResto, m_intRestoPos, arrDestino, intFeitos * 4, intQtd * 4);
                m_intRestoPos += intQtd * 4;
                intFeitos += intQtd;
            }
            return intFeitos;
        }

        private byte[] ProximoBloco()
        {
            while (true)
            {
                if (m_objLeitor != null)
                {
                    byte[] b = m_objLeitor.ProximoBlocoBytes();
                    if (b != null) return b;
                    if (!m_objLeitor.Terminou) throw new IOException(m_objLeitor.Falha ?? "erro de decodificação");
                    Fechar();
                }
                m_intAtual++;
                if (m_intAtual >= m_lstArquivos.Count) return null;
                m_objLeitor = new LeitorAudio();
                if (!m_objLeitor.Abrir(m_lstArquivos[m_intAtual])) throw new IOException(m_objLeitor.Falha);
            }
        }

        private void Fechar()
        {
            if (m_objLeitor != null) { m_objLeitor.Dispose(); m_objLeitor = null; }
        }

        public void Dispose() { Fechar(); }
    }
}
