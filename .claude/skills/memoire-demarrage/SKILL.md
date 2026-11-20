---
name: memoire-demarrage
description: Reprendre un projet en début de session ou avant une tâche d'architecture : lire l'état et l'index des décisions, vérifier qu'ils sont encore valides, résumer où on en est. Nécessite docs/memoire/.
---

# Reprise de session

Si `docs/memoire/` n'existe pas, proposer `memoire-init` sans bloquer la tâche.

## Procédure

1. Lire `docs/memoire/ETAT.md`, puis l'**index** de `docs/memoire/DECISIONS.md` en entier.
2. Ouvrir le détail seulement des décisions liées à la tâche demandée, en suivant les liens `dépend de` et `remplace`. Ne pas lire tout le fichier.
3. Contrôle de fraîcheur rapide sur ces décisions actives :
   - les chemins du champ `Code` existent-ils encore (`test -e`, `git log -1 -- <chemin>`) ?
   - la condition `À réévaluer si` semble-t-elle remplie par la tâche ou par l'état du code ?
   - des faits `[supposé]` pèsent-ils sur la tâche ? Les signaler.
4. Résumer en 2 à 4 lignes : où on en est, prochaine étape, décisions qui contraignent la tâche, anomalies détectées.

## Règles

- En cas de contradiction entre le journal et le code réel, **croire le code**, signaler l'écart, et proposer la correction du journal (via `memoire-decision` ou `memoire-audit`). Ne pas corriger en silence.
- Ne rien écrire pendant la reprise : lecture seule.
- Ne pas bloquer la tâche si les fichiers sont absents ou incomplets.
