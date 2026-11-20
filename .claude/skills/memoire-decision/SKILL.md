---
name: memoire-decision
description: Consigner une décision durable ou une piste abandonnée dans docs/memoire/ avec identifiant, liens, ancrage dans le code et condition de remise en cause. À utiliser dès qu'un choix technique est tranché, pas en fin de session.
---

# Consigner une décision ou une piste

Format exact : voir `docs/memoire/README.md` (source unique). Si le fichier est absent, utiliser d'abord `memoire-init`.

## Quand consigner

- un choix entre plusieurs approches (librairie, schéma, architecture, convention) ;
- une piste testée puis abandonnée ;
- une contrainte découverte (bug de dépendance, limite d'API, comportement surprenant) ;
- une hypothèse qui s'est révélée fausse.

Ne pas consigner : un détail visible dans le diff, un choix trivial et réversible, une idée non tranchée (elle va dans « Questions ouvertes » d'`ETAT.md`).

## Procédure

1. Lire l'index de `DECISIONS.md` pour trouver le prochain identifiant (max + 1, jamais de réutilisation) et les décisions voisines.
2. Rédiger le détail : Date, Contexte, Décision, Alternatives écartées (option → raison), Conséquences, Liens, Code, À réévaluer si.
3. Renseigner les liens avec le vocabulaire exact (`dépend de`, `remplace`, `invalide`, `motivée par`). Un lien doit refléter une vraie relation ; n'en inventer aucun.
4. Étiqueter les faits clés `[vérifié]` ou `[supposé]` selon qu'ils ont été mesurés ou testés, ou seulement déduits ou lus.
5. Ajouter la ligne d'index en haut de l'index, et le détail dans le même ordre.
6. Si la décision en remplace une autre : passer l'ancienne en `remplacée par D0xx` (index et détail) au lieu de la supprimer.
7. Pour une piste abandonnée : ajouter `P0xx — date — idée : raison` dans `PISTES-ECARTEES.md`, et lier la décision qui en découle avec `motivée par`.
8. Confirmer en une ligne à l'utilisateur ce qui a été ajouté.

## Règles

- Écrire le pourquoi, jamais le quoi. Quelques lignes par entrée.
- Ne rien inventer : si la raison, les alternatives ou le code concerné ne sont pas connus, poser la question ou noter `[supposé]`, ne pas combler.
- Aucun secret ni token.
