---
layout: home

hero:
  name: MIDA
  text: .NET Monorepo Multi Tool
  tagline: A CI tool that restores what has not changed instead of building it, and runs only the tests that have not passed.
  actions:
    - theme: brand
      text: Get started
      link: /start
    - theme: alt
      text: Design
      link: /design
    - theme: alt
      text: GitHub
      link: https://github.com/spacentropy/MIDA

features:
  - title: Output cache
    details: Each project gets a key from its inputs. On a hit its outputs are restored from local disk or Google Cloud Storage instead of built.
    link: /notes/cache-key
  - title: Any checkout path
    details: Outputs restore at any checkout path, so a clean CI clone of an unchanged repo restores and builds nothing.
    link: /notes/relocation
  - title: Selective tests
    details: mida test runs only the test projects that have not passed with their current inputs.
    link: /notes/mida-test
---
