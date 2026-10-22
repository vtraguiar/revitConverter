# RevitConversor (pacote para a nuvem)

Código que roda **dentro do Revit Automation da Autodesk** (não é instalado no computador).
O aplicativo `../VerificadorRevit` envia este pacote junto com o projeto:

1. motor Revit 2026: `Exporter` lê o `.rvt` e grava `model.conv.json.gz`;
2. motor Revit 2025: `Importer` reconstrói o modelo num projeto novo e grava `result.rvt` + `relatorio.txt`.

Compilar (gera `../VerificadorRevit/nuvem/ConvRevit<ano>.zip`):
```
dotnet build -c Release -p:RevitVersion=2026
dotnet build -c Release -p:RevitVersion=2025
```
