# revitConverter

Programa para Windows que mostra em que versão um arquivo do Revit foi salvo e, pela nuvem da Autodesk (APS),
converte projetos do Revit 2026 para o 2025 ou exporta para IFC. Não precisa ter o Revit instalado.

- [`VerificadorRevit/`](VerificadorRevit/README.md): o aplicativo (WinForms, .NET Framework 4.8). Instruções de uso e das chaves da Autodesk.
- [`RevitConversor/`](RevitConversor/README.md): o código que roda dentro do Revit na nuvem (exporta no 2026, reconstrói no 2025).

O Revit não salva para versões anteriores: a conversão para o 2025 **reconstrói** o modelo e tem perdas
(famílias sem equivalente viram formas sem parametria; folhas e anotações não voltam). O relatório gerado lista tudo.
