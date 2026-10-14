import { useState } from "react";

const numberRow = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
const letterRows = [
  ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "å"],
  ["a", "s", "d", "f", "g", "h", "j", "k", "l", "æ", "ø"],
  ["z", "x", "c", "v", "b", "n", "m"],
];
const symbolRows = [
  ["!", "@", "#", "$", "%", "&", "*", "(", ")", "-", "_"],
  ["+", "=", ".", ",", "?", "/", "'", "\"", ":", ";"],
];

export function DanishKeyboard({
  uppercase,
  onKey,
  onBackspace,
  onClear,
  onSpace,
  onShift,
}: {
  uppercase: boolean;
  onKey: (key: string) => void;
  onBackspace: () => void;
  onClear: () => void;
  onSpace: () => void;
  onShift: () => void;
}) {
  const [symbols, setSymbols] = useState(false);
  const rows = symbols ? symbolRows : letterRows;

  return (
    <div className="keyboard" role="group" aria-label="Danish keyboard">
      <div className="key-row">
        {numberRow.map((key) => (
          <button key={key} type="button" className="key" onClick={() => onKey(key)}>
            {key}
          </button>
        ))}
      </div>
      {rows.map((row) => (
        <div className="key-row" key={row.join()}>
          {row.map((key) => {
            const label = symbols ? key : uppercase ? key.toLocaleUpperCase("da-DK") : key;
            return (
              <button key={key} type="button" className="key" onClick={() => onKey(label)}>
                {label}
              </button>
            );
          })}
        </div>
      ))}
      <div className="key-row">
        <button type="button" className={symbols ? "key wide active" : "key wide"} onClick={() => setSymbols((value) => !value)}>
          Tegn
        </button>
        <button type="button" className="key wide" onClick={onShift}>
          {uppercase ? "ABC" : "Shift"}
        </button>
        <button type="button" className="key space" onClick={onSpace}>
          space
        </button>
        <button type="button" className="key wide" onClick={onBackspace}>
          Delete
        </button>
        <button type="button" className="key wide" onClick={onClear}>
          Clear
        </button>
      </div>
    </div>
  );
}
