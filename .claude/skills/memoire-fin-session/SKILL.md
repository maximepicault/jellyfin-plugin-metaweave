---
name: memoire-fin-session
description: Clôturer une session ou une tâche : mettre à jour ETAT.md, vérifier que les décisions prises ont été consignées, condenser si les fichiers grossissent. À utiliser en fin de session ou de tâche.
---

# Clôture de session

Format de référence : `docs/memoire/README.md`.

## Procédure

1. **Retrouver ce qui s'est passé** : relire la conversation et, si utile, `git status` et `git diff --stat` ou `git log` récent.
2. **Décisions non consignées** : lister les choix techniques de la session qui ne figurent pas dans l'index. Les consigner avec `memoire-decision`, ou demander à l'utilisateur les informations manquantes plutôt que de les deviner.
3. **Mettre à jour `ETAT.md`** :
   - déplacer le terminé dans « Fait », en gardant l'essentiel ;
   - réécrire « En cours » et « Prochaines étapes » de façon actionnable : quelqu'un sans le contexte doit pouvoir reprendre sans poser de question ;
   - ajouter les « Pièges découverts » et les « Questions ouvertes » ;
   - mettre à jour la date de dernière mise à jour.
4. **Taille** : si `ETAT.md` dépasse ~40 lignes ou l'index ~60 lignes, condenser comme indiqué dans le README (archive-AAAA.md).
5. Dire à l'utilisateur, en 2 lignes, ce qui a été mis à jour. Suggérer de commiter `docs/memoire/` avec le reste du travail.

## Règles

- Rester factuel : n'écrire que ce qui a réellement été décidé ou fait.
- Ne pas faire de journal minute par minute : l'état présent et les prochaines étapes, pas l'historique complet.
