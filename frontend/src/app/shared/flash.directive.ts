import { Directive, ElementRef, effect, inject, input } from '@angular/core';

/** Briefly tints a cell when its live value moves. Direction is also shown by the signed change next to it. */
@Directive({ selector: '[appFlash]' })
export class FlashDirective {
  readonly appFlash = input<number | null | undefined>();
  private el = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private previous: number | null | undefined;

  constructor() {
    effect(() => {
      const value = this.appFlash();
      const before = this.previous;
      this.previous = value;
      if (before === null || before === undefined || value === null || value === undefined || before === value) return;

      this.el.classList.remove('flash-up', 'flash-down');
      void this.el.offsetWidth; // restart the animation if it's mid-flight
      this.el.classList.add(value > before ? 'flash-up' : 'flash-down');
    });
  }
}
