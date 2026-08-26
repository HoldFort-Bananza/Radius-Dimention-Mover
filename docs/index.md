# Radius Dimension Mover — dokumentacja

Aplikacja (.exe) dla **Tekla Structures 2025**. Jedno kliknięcie rozstawia
wszystkie wymiary promieni (`R…`) na otwartym rysunku tak, żeby ich teksty nie
wpadały w obrys części ani w inne opisy, i delikatnie odsuwa opisy otworów,
które leżałyby na linii odniesienia wymiaru.

> **Przejmujesz ten projekt?** Przeczytaj w tej kolejności:
> [Jak to działa](02-algorytm.md) → [Fakty o API Tekli](03-api-tekli.md) →
> [Slepe uliczki](04-slepe-uliczki.md). Trzecia strona jest najważniejsza:
> opisuje podejścia, które **nie działają**, wraz z dowodami. Bez niej
> prawdopodobnie powtórzysz kilka dni pracy.

---

## Spis treści

| Strona | Co zawiera |
|---|---|
| [1. Architektura](01-architektura.md) | Pliki, klasy, przepływ sterowania |
| [2. Jak to działa](02-algorytm.md) | Algorytm rozstawiania, krok po kroku, z geometrią |
| [3. Fakty o API Tekli](03-api-tekli.md) | Jednostki, co API daje, a czego **nie** daje |
| [4. Ślepe uliczki](04-slepe-uliczki.md) | Co próbowano i dlaczego nie działa — **czytaj przed zmianami** |
| [5. Parametry](05-parametry.md) | Każda stała: co robi, jak bezpiecznie zmienić |
| [6. Budowanie i wydawanie](06-budowanie.md) | Kompilacja, instalator, GitHub Release |
| [7. Diagnostyka](07-diagnostyka.md) | Jak podejrzeć dane rysunku, jak testować |
| [8. Znane ograniczenia](08-ograniczenia.md) | Czego program nie potrafi i dlaczego |

---

## Skrót w trzech zdaniach

1. Program łączy się z uruchomioną Teklą przez **Tekla Open API** i czyta
   aktywny rysunek. Nie robi zrzutów ekranu ani analizy pikseli — wszystkie
   decyzje wynikają ze współrzędnych i właściwości obiektów.
2. Dla każdego wymiaru R liczy geometrię łuku (`ArcPoint1/2/3` → środek i
   promień) oraz pobiera z modelu dane części (wymiary bryły, liczba otworów),
   i na tej podstawie **jednym rachunkiem** ustala położenie — bez iteracji,
   bez prób „aż się uda".
3. Reguła: część **bez otworów i dostatecznie szeroka** → tekst zostaje w
   środku; **z otworem albo wąska** → tekst na zewnątrz, przy liniach
   wymiarowych. Potem opisy kolidujące z linią odniesienia są odsuwane w bok
   o dokładnie brakującą różnicę.

## Najważniejsze, co trzeba wiedzieć

**`RadiusDimension` nie udostępnia przez API swojej pozycji na rysunku.**
Nie ma bounding boxa, `ArcPoint1/2/3` opisują sam łuk i nie zmieniają się przy
przesuwaniu tekstu, `GetRelatedObjects()` zwraca pustkę. Program ustawia
`Distance` i **nie ma jak sprawdzić, gdzie tekst faktycznie wylądował**.

Z tego wynikają prawie wszystkie kompromisy w tym projekcie — w tym to, że
`Distance` nie przekłada się wprost na odległość tekstu (przy `Distance=23`
tekst odjechał ~100 mm od łuku) oraz że umieszczanie wewnątrz części jest
dopuszczone tylko dla szerokich blach. Szczegóły:
[Znane ograniczenia](08-ograniczenia.md).

## Repozytorium

- Kod: [github.com/HoldFort-Bananza/Radius-Dimention-Mover](https://github.com/HoldFort-Bananza/Radius-Dimention-Mover)
- Instalator: [Releases](https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/releases)
