# DäckHem – kontroll av flerbilsbokning

Denna PR får **inte** mergas automatiskt. Ingen ändring på den live publicerade sidan görs förrän användaren godkänner.

## Säkerhetsprincip
- Befintliga `POST /api/bookings`, `POST /api/bookings/cancel`, e-postkö för en bil och gamla SQLite-rader lämnas intakta.
- Nya `BookingOrderVehicles` och `BookingOrderEmailOutbox` skapas endast med `CREATE TABLE IF NOT EXISTS`.
- Nytt `POST /api/bookings/multi` använder en SQLite-transaktion för samtliga fordon och en krypterad mejlköpost. Vid fel sker rollback.
- En bil = en normal bokningsrad och en unik avbokningsnyckel, med samma kundorder-ID.
- Varje bils pris och vald total kontrolleras på servern. Ingen mängdrabatt.

## Manuell testmatris före merge
1. En bil med en adress och framtida tid. Kontrollera pris, bokningsbekräftelse, adminvisning, mejl och avbokning.
2. Två bilar på olika adresser, med olika priser (t.ex. 350 + 550 = 900 kr) och olika datum. Kontrollera atomisk lagring, två adminrader och gemensamt mejl.
3. Två bilar på samma adress men olika tider. Båda visas med rätt pris.
4. Fem bilar; plusknappen blir avaktiverad. Minusknappen stoppar vid en.
5. Ändra adress efter prisberäkning; summan får inte användas innan nytt pris kommit.
6. Låtsas ändra priset i request; servern svarar 409 och ingen bokning eller mejl skapas.
7. Ett Google Routes-fel på någon adress; hela ordern nekas utan delvis sparade bilar.
8. Försök boka tid i dåtid; inget sparas.
9. Kunden får **ett** samlat mejl med separata avbokningslänkar; ägaren får ett samlat mejl.
10. Avboka bara bil 1; bil 2 och dess bokning förblir aktiva.
11. Adminpanelen behåller kalendern, arbetsstatus, kommentarer och separata rader för varje bil.
12. Befintliga gamla bokningar, gamla avbokningslänkar och gamla kundmejl påverkas inte.
13. Verifiera mobil och dator, tangentbord, formulärvalidering och responsiv summering.
14. Kontrollera Railway-build och startup **innan** någon aktiverar ny sida live.

## Status
Frontend-JavaScript har genomgått syntaxkontroll och code-review av logiska kopplingar. C# har granskats statiskt men **inte kompilerats eller integrationstestats** i detta steg eftersom SDK/runtime-testmiljö saknas och tidigare GitHub Actions varit blockerad. Kör säker bygg- och integrationstest i isolerad miljö före merge.

## Reserv
Den tidigare fungerande bokningssidan finns kvar i `wwwroot/boka-klassisk.html` för felsökning. Den publicerade `master` är helt oförändrad tills PR mergas.
