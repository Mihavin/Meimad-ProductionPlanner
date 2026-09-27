"use strict";

/* global CodeMirror */

(() => {
  if (typeof CodeMirror === "undefined") {
    return;
  }

  CodeMirror.defineMode("fanuc-nc", () => ({
    startState() {
      return { commentDepth: 0 };
    },

    token(stream, state) {
      if (state.commentDepth > 0) {
        while (!stream.eol()) {
          const character = stream.next();
          if (character === "(") {
            state.commentDepth += 1;
          } else if (character === ")") {
            state.commentDepth -= 1;
            if (state.commentDepth === 0) {
              break;
            }
          }
        }
        return "comment";
      }

      if (stream.eatSpace()) {
        return null;
      }
      if (stream.peek() === ";") {
        stream.skipToEnd();
        return "comment";
      }
      if (stream.peek() === "(") {
        state.commentDepth = 1;
        while (!stream.eol()) {
          const character = stream.next();
          if (character === "(" && stream.current().length > 1) {
            state.commentDepth += 1;
          } else if (character === ")") {
            state.commentDepth -= 1;
            if (state.commentDepth === 0) {
              break;
            }
          }
        }
        return "comment";
      }
      if (stream.match(/^%/)) {
        return "meta";
      }
      if (stream.match(/^(?:O|N)\s*\d+/i)) {
        return "def";
      }
      if (stream.match(/^#(?:\d+(?:\.\d+)?|\[[^\]]*\])/i)) {
        return "variable-2";
      }
      if (stream.match(/^(?:G|M)\s*\d+(?:\.\d+)?/i)) {
        return "keyword";
      }
      if (stream.match(/^T\s*\d+/i)) {
        return "variable-3";
      }
      // Haas writes GOTO999, DO1 and END1 without a space.
      if (stream.match(/^(?:IF|THEN|GOTO|WHILE|WH|DO|END)(?=\s|\d|\[|#|$)/i) ||
          stream.match(/^(?:AND|OR|XOR|EQ|NE|GT|GE|LT|LE|MOD)(?![A-Z])/i)) {
        return "operator";
      }
      if (stream.match(/^(?:SIN|COS|TAN|ASIN|ACOS|ATAN|SQRT|ABS|FIX|FUP|ROUND|LN|EXP|POW|MIN|MAX|ADP|PRM|BIN|BCD)\b/i)) {
        return "builtin";
      }
      if (stream.match(/^[A-Z]\s*[+\-]?(?:\d+(?:\.\d*)?|\.\d+)/i)) {
        return "number";
      }
      if (stream.match(/^[+\-*\/=[\],]/)) {
        return "operator";
      }
      if (stream.match(/^[+\-]?(?:\d+(?:\.\d*)?|\.\d+)/)) {
        return "number";
      }

      stream.next();
      return null;
    }
  }));

  CodeMirror.defineMIME("text/x-fanuc-nc", "fanuc-nc");
})();
