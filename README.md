# Radius Dimension Mover — Tekla Structures 2025

Samodzielna aplikacja (.exe). Jedno kliknięcie porządkuje **wszystkie wymiary
promieni** (`R…`) na otwartym rysunku.

> 📖 **[Dokumentacja projektu — Wiki](https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki)**
>
> Cała wiedza techniczna jest tam i **tylko tam**: algorytm, fakty o API Tekli,
> parametry, budowanie, diagnostyka, znane ograniczenia oraz — najważniejsze —
> **[ślepe uliczki](https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki/4-Slepe-uliczki)**,
> czyli czternaście podejść, które zostały zaimplementowane, zmierzone na żywych
> rysunkach i **nie działają**.
>
> Ten plik celowo nie powtarza szczegółów — README i wiki rozjeżdżały się
> wcześniej i README zaczął kłamać.

## Co robi

- Tekst wymiaru nie wpada w obrys części, w linie wymiarowe ani w inne opisy.
- **Wcięcia** (łuki wklęsłe) dostają tekst po właściwej stronie, a nie w materiale.
- Wymiary idące w tę samą stronę są wyrównane do jednej linii.
- Opisy otworów, które leżałyby na linii odniesienia, są delikatnie odsuwane.
- Dwa teksty, które wylądowałyby jeden na drugim, są rozsuwane — a gdy każde
  rozsunięcie byłoby gorsze, program **nie rusza niczego** i mówi o tym w logu.

Wszystko liczone **analitycznie, jednym przejściem** — bez prób „aż się uda".
Efekt jest natychmiastowy.

**Program działa wyłącznie na danych z Tekla Open API.** Nie robi zrzutów
ekranu, nie analizuje pikseli, nie czyta okna Tekli.

## Wymagania

- Tekla Structures **2025** na tym samym komputerze, z ważną licencją.
- Internet podczas instalacji (instalator pobiera biblioteki Tekla Open API).

## Instalacja

1. Pobierz `RadiusDimensionMover-Setup-vX.Y.exe` z [Releases](../../releases).
2. Uruchom i zaakceptuj pokazaną licencję (EULA Trimble/Tekla).
3. Instalator pobierze biblioteki Tekla Open API świeżo z
   [nuget.org](https://nuget.org) — pod Twoją własną licencją Tekli, dokładnie
   to samo co zrobiłby `dotnet restore`, tylko zautomatyzowane.

> Biblioteki Tekli **nie są dołączone** do repozytorium ani do instalatora. Są
> własnością Trimble/Tekla, a ich licencja zabrania redystrybucji stronom
> trzecim — to repozytorium jest publiczne. Źródła instalatora są w pełni jawne
> w katalogu `installer/`.

## Użycie

1. Uruchom Teklę, otwórz model i rysunek pojedynczej części.
2. Uruchom Radius Dimension Mover.
3. Kliknij **„Przesuń wszystkie wymiary R (unikaj kolizji)"**. Bez parametrów.
4. Log w oknie pokaże, co program zrobił i **dlaczego** — w tym przypadki, w
   których świadomie nic nie ruszył.
5. Sprawdź wzrokowo w Tekli. Coś nie tak? **Ctrl+Z** w Tekli.

Przycisk jest **zawsze klikalny**, a ponowne kliknięcie nic nie psuje — wymiary
są rozstawiane od nowa z geometrii. Log każdej sesji ląduje w
`logs\session_<data_godzina>.log` obok pliku `.exe`.

## Budowanie z kodu

```bash
dotnet build RadiusDimensionMover.csproj -c Debug -p:Platform=x64
```

Instalator: zainstaluj [Inno Setup](https://jrsoftware.org/isinfo.php), zbuduj
projekt, potem `ISCC.exe installer\setup.iss`.

Szczegóły, pułapki i procedura wydawania wersji:
**[Wiki → Budowanie](https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki/6-Budowanie)**.

## Struktura

| Plik | Zawartość |
|---|---|
| `RadiusDimensionService.cs` | cała logika, zero wiedzy o UI |
| `MainForm.cs` | UI, zdarzenia Tekli, sprawdzanie aktualizacji |
| `UpdateCheck.cs` | powiadomienie o nowszym release z GitHuba |
| `Program.cs` | wyłącznie punkt wejścia |
| `CLAUDE.md` | reguły projektu dla asystentów AI (patrz też `..\CLAUDE.md`) |
| `installer/` | Inno Setup i skrypt pobierający biblioteki Tekli |
