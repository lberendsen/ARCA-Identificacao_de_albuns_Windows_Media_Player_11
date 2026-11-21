using System;
using System.Collections.Generic;
using System.IO;
using ARCA.Interop;

namespace ARCA.Modelo
{
    /// <summary>
    /// A meta do ARCA é bit-perfect: cada faixa no acervo igual, amostra por
    /// amostra, à do CD. São três elos, e cada um tem a sua prova:
    ///
    /// 1. **Ler o disco.** O drive pode entregar um trecho estimado sem
    ///    acusar erro. Prova: o AccurateRip — a assinatura da faixa bate com a
    ///    de quem ripou o mesmo disco. Fora da base, a melhor evidência local:
    ///    ler de novo e comparar setor a setor.
    /// 2. **Pôr cada amostra no lugar.** Todo drive lê deslocado por um número
    ///    fixo de amostras (o offset). Correção: o offset do modelo, aplicado
    ///    ao cortar as faixas.
    /// 3. **Codificar.** WMA Lossless é sem perda por definição; aqui é
    ///    conferido: o WMA decodificado tem de ter o mesmo SHA-256 do áudio
    ///    que entrou no codificador.
    ///
    /// Preparar roda entre a leitura e o corte (decide o offset); Concluir,
    /// depois do corte (registra a conferência dos WMA, grava etiquetas e o
    /// relatório).
    /// </summary>
    public static class ConferenciaBitPerfect
    {
        /// <summary>
        /// Offset do drive, AccurateRip sobre a imagem crua e releitura das
        /// faixas que ele não confirmar. Devolve a correção de offset a
        /// aplicar no corte. Preenche objRel.
        /// </summary>
        public static int Preparar(SptiImageExtractor objExtrator, char chrDrive, string strPasta, TRelatorioExtracao objRel,
                                   Action<string> relatar, Action<string, string> fase, Action<string, double, string> progresso)
        {
            string strCue = Path.Combine(strPasta, "AudioCD.cue"), strBin = Path.Combine(strPasta, "AudioCD.bin");
            TTocAudio objToc = File.Exists(strBin) ? TTocAudio.DeCue(strCue, new FileInfo(strBin).Length) : null;
            if (objToc == null) { relatar("Conferência bit-perfect: não consegui montar o índice do disco a partir do CUE."); return 0; }

            // ---- offset do drive ----
            objRel.strDrive = ((objExtrator.Fabricante ?? "").Trim() + " " + (objExtrator.Modelo ?? "").Trim()).Trim();
            string strOrigem;
            int? intOffsetDrive = OffsetDrive.Obter(objExtrator.Fabricante, objExtrator.Modelo, out strOrigem);
            relatar(intOffsetDrive.HasValue
                ? string.Format("Offset do drive {0}: {1:+0;-0;0} amostras ({2}).", objRel.strDrive, intOffsetDrive.Value, strOrigem)
                : string.Format("Offset do drive {0}: desconhecido — vou tentar deduzir pelo AccurateRip.", objRel.strDrive));

            // ---- AccurateRip sobre a leitura crua ----
            if (fase != null) fase("CONFERINDO NO ACCURATERIP…", "Assinatura de cada faixa contra a base pública");
            TResultadoAccurateRip objAr;
            using (TFonteBin objFonte = new TFonteBin(strBin))
                objAr = AccurateRip.Conferir(objFonte, objToc, intOffsetDrive.HasValue ? new[] { intOffsetDrive.Value } : null, null);

            if (objAr.strFalha != null)
            {
                objRel.strAccurateRip = "não consultado: " + objAr.strFalha;
                relatar("AccurateRip: " + objAr.strFalha);
            }
            else if (!objAr.blnNaBase)
            {
                objRel.strAccurateRip = "fora da base";
                relatar("AccurateRip: este disco não está na base. As faixas serão lidas de novo para comparação.");
                foreach (TFaixaAccurateRip f in objAr.lstFaixas) objRel.Verificacao(f.intNumero).intConfiancaAr = 0;
            }
            else
            {
                objRel.strAccurateRip = "na base";
                objRel.intOffsetAccurateRip = objAr.intOffset;
                foreach (TFaixaAccurateRip f in objAr.lstFaixas)
                {
                    TVerificacaoFaixa v = objRel.Verificacao(f.intNumero);
                    v.intConfiancaAr = f.intConfianca;
                    v.intTotalAr = f.intTotalNaBase;
                    v.strVersaoAr = f.strVersao;
                }
                relatar(string.Format("AccurateRip: {0} de {1} faixa(s) conferem (deslocamento {2:+0;-0;0}).",
                                      objAr.QtdConferem, objAr.lstFaixas.Count, objAr.intOffset));
            }

            // ---- decidir a correção ----
            int intAplicar = 0;
            if (intOffsetDrive.HasValue)
            {
                intAplicar = intOffsetDrive.Value;
                objRel.strOrigemOffset = strOrigem;
                // Tabela confirmada por um disco inteiro: passa a ser do drive, medido aqui.
                if (objAr.TodasConferem && objAr.intOffset == intOffsetDrive.Value && strOrigem == "tabela do AccurateRip")
                {
                    try { OffsetDrive.Registrar(objExtrator.Fabricante, objExtrator.Modelo, intAplicar, "confirmado com o AccurateRip em " + DateTime.Now.ToString("dd/MM/yyyy")); }
                    catch { }
                }
                if (objAr.QtdConferem > 0 && objAr.intOffset != intAplicar)
                    relatar(string.Format("A base confere no deslocamento {0:+0;-0;0}: é outra prensagem do mesmo disco. A leitura está certa; o corte usa o offset do drive.", objAr.intOffset));
            }
            else if (objAr.QtdConferem > objAr.lstFaixas.Count / 2)
            {
                // Sem offset conhecido, o que a base mostra é o melhor que há
                // — vale para este disco, e não fica gravado (pode ser
                // prensagem diferente, e não o drive).
                intAplicar = objAr.intOffset;
                objRel.strOrigemOffset = "deduzido do AccurateRip neste disco";
                relatar(string.Format("Offset deduzido do AccurateRip: {0:+0;-0;0} amostras.", intAplicar));
            }
            else
                relatar("ATENÇÃO: offset do drive desconhecido. As faixas sairão sem correção, deslocadas por algumas amostras.");
            objRel.intOffsetAplicado = intAplicar;

            // ---- releitura do que a base não confirma ----
            List<int[]> lstReler = new List<int[]>();
            int intFolga = Math.Abs(intAplicar) / TTocAudio.AmostrasPorSetor + 1;   // o corte corrigido pega setores vizinhos
            for (int t = 0; t < objToc.QtdFaixas; t++)
            {
                int intNum = objToc.intPrimeiraFaixa + t;
                TVerificacaoFaixa v = objRel.Verificacao(intNum);
                if (v.intConfiancaAr > 0) continue;
                lstReler.Add(new[] { intNum, Math.Max(0, objToc.lstInicios[t] - intFolga), objToc.FimDaFaixa(t) + intFolga });
            }
            // Faixa 00 (áudio antes da faixa 1): o AccurateRip não a cobre; com som,
            // vai sempre para a releitura.
            if (objToc.intDadosAntes < 0 && objToc.lstInicios[0] >= ProcessadorExtracaoAudio.SetoresMinimosFaixaOculta)
            {
                double dblPico;
                if (ProcessadorExtracaoAudio.TrechoTemSom(strBin, (long)intAplicar * 4, (long)objToc.lstInicios[0] * 2352, out dblPico))
                {
                    objRel.Verificacao(0);
                    lstReler.Insert(0, new[] { 0, 0, objToc.lstInicios[0] + intFolga });
                }
            }
            if (lstReler.Count > 0)
            {
                relatar(string.Format("Relendo {0} faixa(s) que o AccurateRip não confirmou, para comparar com a primeira leitura...", lstReler.Count));
                if (fase != null) fase("RELENDO FAIXAS…", "Segunda leitura para comparar setor a setor");
                string strFalha;
                List<SptiImageExtractor.TReleitura> lstRes = objExtrator.Reler(chrDrive.ToString(), strBin, lstReler,
                    delegate(int intFaixa, double dblPct)
                    {
                        if (progresso != null) progresso("RELENDO FAIXAS", dblPct, string.Format("Faixa {0:D2}", intFaixa));
                    }, null, out strFalha);
                if (lstRes == null) relatar("Releitura: " + strFalha);
                else
                    foreach (SptiImageExtractor.TReleitura r in lstRes)
                    {
                        TVerificacaoFaixa v = objRel.Verificacao(r.intFaixa);
                        v.intReleituraDiferentes = r.intSetoresDiferentes;
                        v.intReleituraIlegiveis = r.intSetoresIlegiveis;
                        relatar(r.Identica
                            ? string.Format("  Faixa {0:D2}: segunda leitura idêntica.", r.intFaixa)
                            : string.Format("  ⚠ Faixa {0:D2}: segunda leitura DIFERENTE ({1} setor(es) diferente(s), {2} ilegível(is)).",
                                            r.intFaixa, r.intSetoresDiferentes, r.intSetoresIlegiveis));
                    }
            }

            foreach (KeyValuePair<int, int> kv in objRel.dicMascaradosPorFaixa)
                objRel.Verificacao(kv.Key).intMascarados = kv.Value;
            return intAplicar;
        }

        /// <summary>
        /// Depois do corte: registra a conferência de cada WMA, grava nas
        /// faixas o índice do CD (WM/MCDI — permite conferir de novo anos
        /// depois, sem o disco), o drive e o veredito (ARCA/RipDrive,
        /// ARCA/Verification), e salva o relatório.
        /// </summary>
        public static void Concluir(string strPasta, TRelatorioExtracao objRel, List<TConferenciaWma> lstWma, Action<string> relatar)
        {
            foreach (TConferenciaWma c in lstWma)
            {
                TVerificacaoFaixa v = objRel.Verificacao(c.intFaixa);
                v.blnWmaIdentico = c.Confere;
                v.strFalhaWma = c.strFalha;
            }

            string strBin = Path.Combine(strPasta, "AudioCD.bin");
            TTocAudio objToc = File.Exists(strBin) ? TTocAudio.DeCue(Path.Combine(strPasta, "AudioCD.cue"), new FileInfo(strBin).Length) : null;
            string strDrive = string.IsNullOrEmpty(objRel.strDrive) ? null
                : objRel.strDrive + (objRel.strOrigemOffset != null ? string.Format(" (offset {0:+0;-0;0})", objRel.intOffsetAplicado) : " (offset desconhecido)");

            foreach (TConferenciaWma c in lstWma)
            {
                if (string.IsNullOrEmpty(c.strArquivoWma) || !File.Exists(c.strArquivoWma)) continue;
                try
                {
                    using (EditorAsf e = new EditorAsf())
                    {
                        if (!e.Abrir(c.strArquivoWma)) continue;
                        e.DefinirTexto("ARCA/Verification", objRel.Verificacao(c.intFaixa).ParaEtiqueta());
                        if (strDrive != null) e.DefinirTexto("ARCA/RipDrive", strDrive);
                        if (objToc != null) e.DefinirMcdi(objToc.ParaMcdi());
                        e.Salvar();
                    }
                }
                catch (Exception ex) { relatar(string.Format("Aviso: não gravei a verificação na faixa {0}: {1}", c.intFaixa, ex.Message)); }
            }

            try { objRel.Gravar(strPasta); }
            catch (Exception ex) { relatar("Aviso: não consegui gravar " + TRelatorioExtracao.NomeArquivo + ": " + ex.Message); }
            relatar("BIT-PERFECT: " + objRel.ResumoBitPerfect() + ".");
        }
    }
}
