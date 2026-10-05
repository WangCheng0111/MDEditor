# Embedded en-US hyphenation data

Upstream: [hyphenation/tex-hyphen · hyph-en-us.tex](https://github.com/hyphenation/tex-hyphen/blob/master/hyph-utf8/tex/generic/hyph-utf8/patterns/tex/hyph-en-us.tex), retrieved 2026-09-18.
Pattern version in upstream header: 2005-05-30. The embedded snapshot is pinned by content, not fetched at runtime.
The repository attributes force LF checkout for this exact resource, including on Windows with autocrlf enabled.

SHA-256 of this repository's UTF-8/LF resource:

`F4FFCD96C5CBC886BDAD23F95DCAE8EDC3CD3620EAE62F7946ECEDA97C4E68F8`

The complete upstream file, including copyright and redistribution notice, is retained in `hyph-en-us.tex`. It contains 4,938 pattern tokens and 14 exception words. Runtime adds the explicit `dem-o-crat` exception to correct the documented upstream issue, without modifying the resource. The resulting engine has 15 exception words.

No generic repository license is substituted for this data's own notice. Changes to the resource must review licensing and update the pinned hash and tests deliberately.

Algorithm reference: [Frank Liang's thesis, TUG](https://tug.org/docs/liang/). US typesetting minima are two letters before and three after a break. This release uses only ASCII English words; other languages, accented words and Unicode presentation-form ligatures are not transliterated.
