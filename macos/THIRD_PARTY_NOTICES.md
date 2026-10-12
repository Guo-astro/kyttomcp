# Third-party notices

## cl100k_base.tiktoken

`KyttoCore/Sources/KyttoCore/Tokenizer/cl100k_base.tiktoken` is the `cl100k_base`
BPE vocabulary distributed with [tiktoken](https://github.com/openai/tiktoken),
used to estimate the token weight of each server's tool definitions.

```
MIT License

Copyright (c) 2022 OpenAI, Shantanu Jain

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## rugsnare corpus 05 (test fixture)

`KyttoCore/Tests/KyttoCoreTests/Fixtures/rugsnare-corpus-05/` holds the `tools/list`
responses of two servers from
[`corpus/05-key-reorder`](https://github.com/Paraphern/rugsnare/tree/main/corpus/05-key-reorder)
in [Paraphern/rugsnare](https://github.com/Paraphern/rugsnare): the same two tool schemas
with every object's keys in reverse order. It is the regression fixture for Contract Guard
reporting a pure re-serialization as a breaking change (issue #12). Copyright 2026 Paraphern,
Apache License 2.0; the licence text is next to the fixtures. Test-only, not part of the app.
