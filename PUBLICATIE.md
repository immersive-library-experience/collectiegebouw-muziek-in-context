# Inhoud van deze publicatie

Gepubliceerd op 2026-11-19 uit de privé-ontwikkelrepository (CollectionKit, commit `4ccba17370`), als momentopname
zonder geschiedenis. Werk in uitvoering staat op `next`; `main` volgt bij de mijlpalen.

- `importers/CollectionKit.Import.Delpher` (3 bestanden)
- `importers/CollectionKit.Import.MusicNews` (2 bestanden)
- `profiles/beeldengeluid` (3 bestanden)
- `tools/check_bundle_ids.py`
- `tools/delpher_paper_titles.py`
- `tools/delpher_pick.py`
- `tools/discogs_keys.py`
- `tools/hoes_koppelingen.py`
- `tools/musicbrainz_keys.py`
- `tools/muziekweb_keys.py`

## Bouwen

Deze repository bouwt naast een kopie van
[collection-experience-core](https://github.com/immersive-library-experience/collection-experience-core), in dezelfde map:

```
git clone https://github.com/immersive-library-experience/collection-experience-core
git clone https://github.com/immersive-library-experience/collectiegebouw-muziek-in-context
dotnet build collectiegebouw-muziek-in-context/collectiegebouw-muziek-in-context.slnx -c Release
```

## Wat hier niet in staat

- **Collectie-inhoud**: de selectie van albums, de teksten, hoezen, krantenartikelen en de gebouwde bundel. De keuze en de
  rechten liggen bij Beeld & Geluid; de importers halen de inhoud bij de bron (MusicBrainz, Muziekweb, Discogs, Delpher).
- **De beelden van de Fonos-huisstijl** (cijfers en iconen): het thema verwijst ernaar, en zonder die map tekent de
  installatie gewone letters.
- **Instellingen en opnames van de testopstellingen.**
