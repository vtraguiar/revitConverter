#!/usr/bin/env bash
# Publica uma versão nova no GitHub Releases; o programa dos usuários se atualiza a partir dela.
#   ./publicar.sh 1.1 "O que mudou nesta versão"
# Requer: gh (logado), zip, sha256sum e o .NET SDK (DOTNET=/caminho/dotnet se não estiver no PATH).
# Os pacotes da nuvem (VerificadorRevit/nuvem/*.zip) só mudam ao recompilar o RevitConversor; faça isso antes, se mexeu nele.
set -euo pipefail
V=${1:?informe a versão, ex.: ./publicar.sh 1.1 "notas"}
NOTES=${2:-"Versão $V"}
DOTNET=${DOTNET:-dotnet}
cd "$(dirname "$0")"

[ -z "$(git status --porcelain)" ] || { echo "Há alterações sem commit; faça o commit antes de publicar."; exit 1; }

"$DOTNET" build -c Release --no-incremental -p:Version="$V" VerificadorRevit >/dev/null

T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
mkdir "$T/ConversorRevit"
cp -r VerificadorRevit/bin/Release/net48/{VerificadorRevit.exe,VerificadorRevit.exe.config,nuvem} "$T/ConversorRevit/"
Z="ConversorRevit-v$V.zip"
rm -f "$Z" "$Z.sha256"
(cd "$T" && zip -qr "$OLDPWD/$Z" ConversorRevit)
sha256sum "$Z" | cut -d' ' -f1 > "$Z.sha256"

git push -q
gh release create "v$V" "$Z" "$Z.sha256" --title "Conversor Revit $V" --notes "$NOTES"
