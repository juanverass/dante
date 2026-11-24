#!/usr/bin/env python3
# EXEMPLO DE DOCUMENTAÇÃO (Issue #241). Verificação estática de onde cada job de um repositório vai rodar.
# Não faz rede nem chama a API do GitHub: lê os arquivos de .github/workflows/ e falha (código 1) quando algum job
# elegível pode cair num runner fora da política do repositório.
#
# Uso:
#   python3 verificar-runs-on.py <raiz-do-repo> --modo self-hosted [--rotulos self-hosted,Linux,X64,docker]
#   python3 verificar-runs-on.py <raiz-do-repo> --modo hosted
#   [--reutilizavel-revisado dono/repo/.github/workflows/x.yml@<sha>]  (repetível)
#
# Modo self-hosted (repositório privado sem cota hosted): todo job, inclusive os de workflows reutilizáveis locais,
# precisa de runs-on com o rótulo self-hosted e só rótulos da lista. Modo hosted (repositório público): nenhum job
# pode pedir self-hosted. Nos dois modos, pull_request_target é recusado e runs-on que não dá para resolver
# estaticamente (expressão, matriz, grupo sem rótulos, workflow reutilizável remoto não revisado) é erro: na dúvida,
# falha.
#
# Requer PyYAML (python3 -m pip install pyyaml, ou o pacote python3-yaml da distribuição).
import argparse
import json
import re
import sys
from pathlib import Path

import yaml

ROTULOS_PADRAO = "self-hosted,Linux,X64,docker"
EXPRESSAO_DO_RUNNER = re.compile(r"^\$\{\{\s*fromJSON\(\s*inputs\.([A-Za-z0-9_-]+)\s*\)\s*\}\}$")


class Verificacao:
    def __init__(self, raiz, modo, rotulos, revisados):
        self.raiz = raiz
        self.modo = modo
        self.rotulos = {r.lower() for r in rotulos}
        self.revisados = set(revisados)
        self.erros = []
        self.avisos = []
        self.jobs = 0

    def erro(self, onde, mensagem):
        self.erros.append(f"ERRO  {onde}: {mensagem}")

    def aviso(self, onde, mensagem):
        self.avisos.append(f"AVISO {onde}: {mensagem}")

    def carregar(self, caminho):
        with open(caminho, encoding="utf-8") as arquivo:
            documento = yaml.safe_load(arquivo)
        if not isinstance(documento, dict):
            raise ValueError("workflow não é um mapa YAML")
        return documento

    def verificar_repositorio(self):
        pasta = self.raiz / ".github" / "workflows"
        arquivos = sorted(list(pasta.glob("*.yml")) + list(pasta.glob("*.yaml"))) if pasta.is_dir() else []
        if not arquivos:
            self.aviso(str(pasta), "nenhum workflow encontrado")
        for caminho in arquivos:
            relativo = caminho.relative_to(self.raiz).as_posix()
            try:
                documento = self.carregar(caminho)
            except (yaml.YAMLError, ValueError) as falha:
                self.erro(relativo, f"YAML inválido ({falha})")
                continue
            # PyYAML 1.1 lê a chave "on" como booleano.
            gatilhos = documento.get("on", documento.get(True))
            if gatilhos_de(gatilhos) == {"workflow_call"}:
                continue  # só reutilizável: verificado a partir de quem chama, com os inputs de quem chama
            self.verificar_gatilhos(relativo, gatilhos)
            if "permissions" not in documento:
                self.aviso(relativo, "sem 'permissions' no topo: vale o padrão do repositório")
            self.verificar_jobs(relativo, documento, entradas={}, pilha=[relativo])

    def verificar_gatilhos(self, onde, gatilhos):
        nomes = gatilhos_de(gatilhos)
        if "pull_request_target" in nomes:
            self.erro(onde, "pull_request_target é proibido pela política (código não confiável com credenciais)")
        if "workflow_run" in nomes:
            self.aviso(onde, "workflow_run roda com credenciais do repositório: revisar se usa artefato ou código de PR")

    def verificar_jobs(self, onde, documento, entradas, pilha):
        jobs = documento.get("jobs")
        if not isinstance(jobs, dict) or not jobs:
            self.erro(onde, "sem jobs")
            return
        for nome, job in jobs.items():
            local = f"{onde}:{nome}"
            if not isinstance(job, dict):
                self.erro(local, "job não é um mapa")
                continue
            if "uses" in job:
                self.verificar_chamada(local, job, pilha)
                continue
            self.jobs += 1
            self.verificar_runs_on(local, job.get("runs-on"), entradas)
            if "timeout-minutes" not in job:
                self.aviso(local, "sem timeout-minutes (padrão da plataforma: 360 min)")

    def verificar_chamada(self, local, job, pilha):
        alvo = str(job["uses"])
        if not alvo.startswith("./"):
            if alvo in self.revisados:
                self.aviso(local, f"reutilizável remoto revisado manualmente: {alvo}")
            else:
                self.erro(local, f"reutilizável remoto define o próprio runs-on: revisar e fixar por SHA ({alvo})")
            return
        caminho = (self.raiz / alvo[2:]).resolve()
        relativo = alvo[2:]
        if relativo in pilha:
            self.erro(local, f"chamada circular: {relativo}")
            return
        if not caminho.is_file() or self.raiz.resolve() not in caminho.parents:
            self.erro(local, f"reutilizável local não encontrado: {alvo}")
            return
        try:
            chamado = self.carregar(caminho)
        except (yaml.YAMLError, ValueError) as falha:
            self.erro(local, f"YAML inválido em {relativo} ({falha})")
            return
        declarados = (chamado.get("on", chamado.get(True)) or {})
        declarados = declarados.get("workflow_call", {}) if isinstance(declarados, dict) else {}
        declarados = (declarados or {}).get("inputs", {}) or {}
        entradas = {}
        for nome, definicao in declarados.items():
            if isinstance(definicao, dict) and "default" in definicao:
                entradas[nome] = definicao["default"]
        entradas.update(job.get("with", {}) or {})
        self.verificar_jobs(f"{local} -> {relativo}", chamado, entradas, pilha + [relativo])

    def verificar_runs_on(self, local, valor, entradas):
        rotulos = self.resolver(local, valor, entradas)
        if rotulos is None:
            return
        minusculos = {r.lower() for r in rotulos}
        if self.modo == "self-hosted":
            fora = sorted(r for r in rotulos if r.lower() not in self.rotulos)
            if "self-hosted" not in minusculos:
                self.erro(local, f"runs-on {rotulos} não é self-hosted: consumiria minutos hosted")
            elif fora:
                self.erro(local, f"rótulos fora da lista permitida: {fora}")
        elif "self-hosted" in minusculos:
            self.erro(local, f"runs-on {rotulos} pede self-hosted num repositório que deve usar só hosted")

    def resolver(self, local, valor, entradas):
        if valor is None:
            self.erro(local, "job sem runs-on")
            return None
        if isinstance(valor, dict):
            if "group" in valor and "labels" not in valor:
                self.erro(local, "runs-on só com group: rótulos não verificáveis estaticamente")
                return None
            valor = valor.get("labels")
        if isinstance(valor, str):
            casamento = EXPRESSAO_DO_RUNNER.match(valor.strip())
            if casamento:
                nome = casamento.group(1)
                bruto = entradas.get(nome)
                if not isinstance(bruto, str) or "${{" in bruto:
                    self.erro(local, f"input '{nome}' do runner ausente ou dinâmico: não verificável")
                    return None
                try:
                    valor = json.loads(bruto)
                except json.JSONDecodeError:
                    self.erro(local, f"input '{nome}' do runner não é JSON: {bruto!r}")
                    return None
            elif "${{" in valor:
                self.erro(local, f"runs-on com expressão não verificável: {valor!r}")
                return None
        if isinstance(valor, str):
            valor = [valor]
        if not isinstance(valor, list) or not valor or not all(isinstance(r, str) and r for r in valor):
            self.erro(local, f"runs-on inválido: {valor!r}")
            return None
        if any("${{" in r for r in valor):
            self.erro(local, f"runs-on com expressão não verificável: {valor!r}")
            return None
        return valor


def gatilhos_de(gatilhos):
    if isinstance(gatilhos, str):
        return {gatilhos}
    if isinstance(gatilhos, list):
        return {str(g) for g in gatilhos}
    if isinstance(gatilhos, dict):
        return {str(g) for g in gatilhos}
    return set()


def main():
    parser = argparse.ArgumentParser(description="Verifica onde cada job dos workflows vai rodar.")
    parser.add_argument("raiz", type=Path, help="raiz do repositório (a que contém .github/)")
    parser.add_argument("--modo", choices=["self-hosted", "hosted"], required=True)
    parser.add_argument("--rotulos", default=ROTULOS_PADRAO, help="rótulos permitidos no modo self-hosted")
    parser.add_argument("--reutilizavel-revisado", action="append", default=[])
    argumentos = parser.parse_args()

    verificacao = Verificacao(
        argumentos.raiz,
        argumentos.modo,
        [r.strip() for r in argumentos.rotulos.split(",") if r.strip()],
        argumentos.reutilizavel_revisado,
    )
    verificacao.verificar_repositorio()
    for linha in verificacao.avisos + verificacao.erros:
        print(linha)
    print(f"{verificacao.jobs} job(s) verificados, {len(verificacao.erros)} erro(s), {len(verificacao.avisos)} aviso(s).")
    return 1 if verificacao.erros else 0


if __name__ == "__main__":
    sys.exit(main())
