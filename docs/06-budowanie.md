[← Spis treści](index.md)

# 6. Budowanie i wydawanie

## Wymagania

- **.NET Framework 4.8** SDK (projekt celuje w `net48`, x64)
- **Tekla Structures 2025** zainstalowana lokalnie (do testów, z licencją)
- **Inno Setup 6** — tylko do budowania instalatora
- Internet przy pierwszym budowaniu (NuGet ściąga pakiety `Tekla.Structures*`)

## Budowanie programu

```bash
dotnet build RadiusDimensionMover.csproj -c Debug -p:Platform=x64
```

Wynik: `bin\x64\Debug\net48\RadiusDimensionMover.exe`

Pakiety NuGet (`Tekla.Structures`, `Tekla.Structures.Drawing`, wersja
`2025.0.0`) pobiorą się same. Przy innej wersji Tekli popraw wersję w
`.csproj`.

> **Ostrzeżenia `MSB3277`** o konflikcie wersji
> `System.Runtime.CompilerServices.Unsafe` są normalne i nieszkodliwe —
> występują od początku projektu.

> **`MSB3027 / MSB3021`** („plik jest zablokowany przez
> RadiusDimensionMover") znaczy tylko, że program jest uruchomiony. Zamknij go:
> `taskkill /F /IM RadiusDimensionMover.exe`

### Dlaczego `TSAppConfigPatcherTask`

Ten pakiet musi zostać w `.csproj`. Bez niego plik `.exe.config` nie jest
poprawnie generowany i `DrawingHandler.GetConnectionStatus()` **zawsze zwraca
false**, nawet gdy Tekla działa z otwartym modelem (architektura „gacless" w
Tekli 2024/2025).

## Budowanie instalatora

```bash
cd installer
ISCC.exe setup.iss
```

Wynik: `installer\output\RadiusDimensionMover-Setup-v1.0.exe` (~2,1 MB)

Jeśli Inno Setup nie jest w `PATH`, typowa ścieżka po instalacji lokalnej:
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`

> Instalator Inno Setup domyślnie chce podnieść uprawnienia (UAC). Przy
> instalacji nieinteraktywnej użyj `/DIR=` wskazującego katalog użytkownika,
> np. `%LOCALAPPDATA%\Programs\Inno Setup 6`, żeby nie czekać na okno UAC.

### Co robi instalator i dlaczego tak

`setup.iss` pakuje **tylko** własny `.exe`, `.exe.config`, `.pdb` i skrypt
`fetch-dependencies.ps1`. Po instalacji uruchamia ten skrypt, który pobiera
biblioteki Tekla Open API **świeżo z nuget.org**.

Powód jest licencyjny: biblioteki są własnością Trimble/Tekla, a ich EULA
zabrania redystrybucji stronom trzecim. Repozytorium jest publiczne, więc nie
mogą w nim leżeć. Instalator automatyzuje tylko ich legalne pobranie na
komputer użytkownika — dokładnie to, co zrobiłby `dotnet restore`.

Instalator pokazuje EULA Trimble/Tekla (`installer/TeklaEULA.txt`) i wymaga
akceptacji.

## Co jest w repozytorium, a co nie

`.gitignore` celowo dopuszcza **zbudowany plik .exe** (i `.pdb`, `.exe.config`)
w `bin/x64/Debug/net48/`, żeby instalator dał się zbudować bez kompilowania.
Wszystko inne z `bin/` i `obj/` jest ignorowane, podobnie
`installer/output/` — skompilowany instalator trafia do GitHub Releases, nie
do gita.

## Wydawanie nowej wersji

Aktualnie wszystko wisi pod jednym tagiem **v1.0** i instalator jest w nim
podmieniany. Krok po kroku:

```bash
# 1. zamknij program, zbuduj
taskkill /F /IM RadiusDimensionMover.exe
dotnet build RadiusDimensionMover.csproj -c Debug -p:Platform=x64

# 2. instalator
cd installer && ISCC.exe setup.iss && cd ..

# 3. commit + push
git add -A
git commit -m "..."
git push origin main
```

Potem podmiana pliku w release. Token bierzemy z magazynu poświadczeń gita —
**nigdy nie wpisujemy go do polecenia ani nie logujemy**:

```bash
GH_TOKEN=$(printf 'protocol=https\nhost=github.com\n\n' | git credential fill \
  | grep '^password=' | cut -d= -f2-)

# id istniejącego pliku
ASSET=$(curl -s "https://api.github.com/repos/HoldFort-Bananza/Radius-Dimention-Mover/releases/376444525" \
  | python -c "import sys,json; print(json.load(sys.stdin)['assets'][0]['id'])")

# usuń stary, wgraj nowy
curl -X DELETE -H "Authorization: token $GH_TOKEN" \
  "https://api.github.com/repos/HoldFort-Bananza/Radius-Dimention-Mover/releases/assets/$ASSET"

curl -X POST -H "Authorization: token $GH_TOKEN" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @"installer/output/RadiusDimensionMover-Setup-v1.0.exe" \
  "https://uploads.github.com/repos/HoldFort-Bananza/Radius-Dimention-Mover/releases/376444525/assets?name=RadiusDimensionMover-Setup-v1.0.exe"

unset GH_TOKEN
```

Release **376444525** = tag `v1.0`. Link do pobrania nie zmienia się przy
podmianie pliku.

Jeśli kiedyś dojdzie prawdziwa wersja 2.0: podnieś `MyAppVersion` w
`setup.iss`, utwórz nowy tag i nowy release, i zaktualizuj to ID.

## Dokumentacja (ta strona)

Źródła są w katalogu `docs/` i publikowane przez **GitHub Pages** z gałęzi
`main`, katalog `/docs`. Każdy `push` na `main` odświeża stronę automatycznie
(kilkadziesiąt sekund).

Po zmianie zachowania programu **zaktualizuj odpowiednią stronę w tym samym
commicie** — dokumentacja rozjechana z kodem jest gorsza niż jej brak.
Najczęściej do ruszenia:

- zmiana progu/stałej → [Parametry](05-parametry.md)
- zmiana logiki → [Algorytm](02-algorytm.md)
- nowe odkrycie o API → [Fakty o API](03-api-tekli.md)
- coś nie zadziałało → [Ślepe uliczki](04-slepe-uliczki.md) **z dowodem**
