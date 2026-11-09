import { describe, expect, it } from 'vitest';
import { readableInk } from './Charts';

describe('readableInk', () => {
  it('picks the label color with the higher contrast for each status color', () => {
    expect(readableInk('#0ca30c')).toBe('#0b0b0b'); // green: white would be 3.4:1
    expect(readableInk('#fab219')).toBe('#0b0b0b');
    expect(readableInk('#d03b3b')).toBe('#ffffff');
    expect(readableInk('#276fc6')).toBe('#ffffff');
  });
});
