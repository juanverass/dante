#!/usr/bin/env python3
"""Calibração offline do corte de relevância da busca híbrida (#280).

Entrada: a matriz de scores gravada pelo harness (`MatrizDeCalibracao`), com os scores
lexical e semântico de todos os itens indexáveis para cada consulta de calibração. Também
lê o corpus versionado para estimar o pacote automático.

O script simula os dois canais com a mesma regra do índice:
- correspondência lexical nunca é cortada;
- o candidato só semântico passa somente se atender à regra de corte;
- a ordem segue o RRF (k = 60) entre os aprovados.

Uso:
    python3 -I calibrar_corte.py <calibracao-matriz.json> <busca-brain.json> [saida.json]

Só as consultas de calibração entram aqui. O conjunto de validação não é lido.
"""
import json
import math
import statistics
import sys

K_RRF = 60
LIMITE_AGENTE = 10
LIMITE_ITENS = 12
ORCAMENTO = 2048
CABECALHO = 75          # tokens estimados do cabeçalho do pacote
SOBRECARGA = 95         # tokens estimados dos metadados JSON de cada item

TAUS = [round(0.30 + 0.01 * i, 2) for i in range(41)]          # 0,30 .. 0,70
DELTAS = [round(0.01 * i, 2) for i in range(1, 31)]             # 0,01 .. 0,30
ZS = [round(0.25 * i, 2) for i in range(0, 17)]                 # 0 .. 4


def tokens(conteudo):
    return (len(conteudo.encode("utf-8")) + 2) // 3 + SOBRECARGA


def prioridade(item):
    if item.get("confirmado") and item["tipo"] == "Instrucao":
        return 0
    if item.get("confirmado") and item["tipo"] == "Decisao":
        return 1
    if item.get("confirmado"):
        return 2
    return 5


def aprovados(itens, campo_sem, regra):
    """Conjunto dos que passam no corte semântico, conforme a regra."""
    tipo, p = regra
    sems = [i[campo_sem] for i in itens]
    if tipo == "R0":
        return {i["chave"] for i in itens if i[campo_sem] > 0}
    if tipo == "R1":
        return {i["chave"] for i in itens if i[campo_sem] >= p["tau"]}
    if tipo == "R2":
        melhor = max(sems)
        return {i["chave"] for i in itens if i[campo_sem] >= p["tau"] and i[campo_sem] >= melhor - p["delta"]}
    if tipo == "R3":
        media, desvio = statistics.fmean(sems), statistics.pstdev(sems) or 1e-9
        return {i["chave"] for i in itens if i[campo_sem] >= p["tau"] and (i[campo_sem] - media) / desvio >= p["z"]}
    raise ValueError(tipo)


def ranquear(itens, campo_lex, campo_sem, regra, exige_semantica=False):
    """exige_semantica: correspondência lexical parcial (OR) só entra com apoio semântico;
    a correspondência com todos os termos da mensagem (lexicalCru) entra sempre."""
    passa = aprovados(itens, campo_sem, regra)
    lex = sorted((i for i in itens if i[campo_lex] > 0 and (not exige_semantica or i["lexicalCru"] > 0 or i["chave"] in passa)),
                 key=lambda i: (-i[campo_lex], i["chave"]))
    sem = sorted((i for i in itens if i["chave"] in passa), key=lambda i: (-i[campo_sem], i["chave"]))
    score = {}
    for pos, i in enumerate(lex, 1):
        score[i["chave"]] = score.get(i["chave"], 0) + 1 / (K_RRF + pos)
    for pos, i in enumerate(sem, 1):
        score[i["chave"]] = score.get(i["chave"], 0) + 1 / (K_RRF + pos)
    return sorted(score, key=lambda k: (-score[k], k)), score


def pacote(ordem, score, corpus):
    candidatos = sorted(ordem, key=lambda k: (prioridade(corpus[k]), -score[k], k))
    usados, total = [], CABECALHO
    for k in candidatos:
        t = tokens(corpus[k]["conteudo"])
        if len(usados) >= LIMITE_ITENS or total + t > ORCAMENTO:
            continue
        usados.append(k)
        total += t
    return usados, (total if usados else 0)


def avaliar(consultas, corpus, regra, campo_sem_auto, exige_semantica=False):
    por = []
    for q in consultas:
        rel = set(q["relevantes"])
        # O canal agente aplica os filtros de tag da consulta antes do ranking, como o índice.
        tags = {t.lower() for t in q.get("tags") or []}
        filtrados = [i for i in q["itens"] if tags <= {t.lower() for t in corpus[i["chave"]]["tags"]}]
        agente, _ = ranquear(filtrados, "lexicalCru", "semanticoCru", regra)
        agente = agente[:LIMITE_AGENTE]
        ordem, score = ranquear(q["itens"], "lexicalTermos", campo_sem_auto, regra, exige_semantica)
        pac, tok = pacote(ordem, score, corpus)
        por.append({
            "id": q["id"], "categoria": q["categoria"], "comResposta": bool(rel),
            "acerto5": any(k in rel for k in agente[:5]),
            "rr": next((1 / (n + 1) for n, k in enumerate(agente) if k in rel), 0.0),
            "resultados": len(agente), "pacote": len(pac), "relevantesNoPacote": sum(k in rel for k in pac),
            "tokens": tok,
        })
    return por


def resumo(por):
    resp = [x for x in por if x["comResposta"]]
    sem = [x for x in por if not x["comResposta"]]
    lit = [x for x in resp if x["categoria"] == "literal"]
    par = [x for x in resp if x["categoria"] in ("parafrase", "outro-idioma")]
    sel = sum(x["pacote"] for x in por)

    def taxa(xs):
        return sum(x["acerto5"] for x in xs) / len(xs) if xs else None

    return {
        "acerto5Literal": taxa(lit), "acerto5Parafrase": taxa(par), "acerto5": taxa(resp),
        "mrr": statistics.fmean(x["rr"] for x in resp) if resp else None,
        "semRespostaComPacote": sum(x["pacote"] > 0 for x in sem),
        "semRespostaComResultado": sum(x["resultados"] > 0 for x in sem),
        "precisaoDoPacote": sum(x["relevantesNoPacote"] for x in por) / sel if sel else 0.0,
        "pacoteComRelevante": sum(x["relevantesNoPacote"] > 0 for x in resp) / len(resp) if resp else None,
        "tokensMedio": statistics.fmean(x["tokens"] for x in por),
    }


def viavel(r):
    """Critérios de decisão registrados antes da medição (#280)."""
    return (r["acerto5Literal"] in (None, 1.0) and (r["acerto5Parafrase"] is None or r["acerto5Parafrase"] >= 0.8)
            and r["semRespostaComPacote"] == 0 and r["precisaoDoPacote"] > 0.323)


def utilidade(r):
    # Entre pontos viáveis, prefere recuperar paráfrases e manter o pacote preciso, em peso igual.
    if not viavel(r):
        return -1.0
    return 0.5 * (r["acerto5Parafrase"] or 0) + 0.5 * r["precisaoDoPacote"]


def grade(tipo):
    if tipo == "R1":
        return [((t,), {"tau": t}) for t in TAUS]
    if tipo == "R2":
        return [((t, d), {"tau": t, "delta": d}) for t in TAUS for d in DELTAS]
    if tipo == "R3":
        return [((t, z), {"tau": t, "z": z}) for t in TAUS for z in ZS]
    raise ValueError(tipo)


def robusta(utils, chave, passos):
    """Pior utilidade na vizinhança de um passo de grade: favorece faixas estáveis."""
    vizinhos = [chave]
    for eixo, valores in enumerate(passos):
        idx = valores.index(chave[eixo])
        for d in (-1, 1):
            if 0 <= idx + d < len(valores):
                v = list(chave)
                v[eixo] = valores[idx + d]
                vizinhos.append(tuple(v))
    return min(utils.get(v, -1.0) for v in vizinhos)


def escolher(consultas, corpus, tipo, campo_sem_auto, exige_semantica=False):
    passos = {"R1": [TAUS], "R2": [TAUS, DELTAS], "R3": [TAUS, ZS]}[tipo]
    pontos = grade(tipo)
    utils, resumos = {}, {}
    for chave, p in pontos:
        r = resumo(avaliar(consultas, corpus, (tipo, p), campo_sem_auto, exige_semantica))
        utils[chave], resumos[chave] = utilidade(r), r
    melhor = max(pontos, key=lambda cp: (robusta(utils, cp[0], passos), utils[cp[0]], -sum(cp[0])))
    return melhor, robusta(utils, melhor[0], passos), resumos, utils


def main():
    matriz = json.load(open(sys.argv[1], encoding="utf-8"))
    corpus = {c["chave"]: c for c in json.load(open(sys.argv[2], encoding="utf-8"))["corpus"]}
    # O harness serializa membros C# (Id, Categoria, Relevantes): normaliza para minúsculas.
    consultas = [{k[0].lower() + k[1:]: v for k, v in q.items()} for q in matriz["consultas"]]
    tags = {q["id"]: q.get("tags") for q in json.load(open(sys.argv[2], encoding="utf-8"))["consultas"]}
    for q in consultas:
        q["tags"] = tags.get(q["id"])
    saida = {"modelo": matriz["modelo"], "variantes": {}}
    combinacoes = [(f"{v}/{'lexicalParcialExigeSemantica' if e else 'lexicalParcialMantida'}", c, e)
                   for v, c in (("A", "semanticoCru"), ("semA", "semanticoTermos")) for e in (False, True)]
    for nome_var, campo, exige in combinacoes:
        var = {"R0": resumo(avaliar(consultas, corpus, ("R0", {}), campo))}
        for tipo in ("R1", "R2", "R3"):
            (chave, p), rob, resumos, utils = escolher(consultas, corpus, tipo, campo, exige)
            viaveis = sorted(k for k, u in utils.items() if u >= 0)
            # Leave-one-out: escolhe com 26 consultas e avalia a que ficou de fora.
            loo, escolhas = [], []
            for i in range(len(consultas)):
                treino = consultas[:i] + consultas[i + 1:]
                (_, pi), _, _, _ = escolher(treino, corpus, tipo, campo, exige)
                escolhas.append(pi)
                loo += avaliar([consultas[i]], corpus, (tipo, pi), campo, exige)
            var[tipo] = {
                "parametros": p, "utilidadeRobusta": rob, "calibracao": resumos[chave],
                "pontosViaveis": len(viaveis), "faixaViavel": {"min": viaveis[0], "max": viaveis[-1]} if viaveis else None,
                "leaveOneOut": resumo(loo), "parametrosLoo": escolhas,
            }
        saida["variantes"][nome_var] = var
    texto = json.dumps(saida, ensure_ascii=False, indent=2)
    if len(sys.argv) > 3:
        open(sys.argv[3], "w", encoding="utf-8").write(texto + "\n")
    print(texto)


if __name__ == "__main__":
    main()
