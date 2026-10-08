import * as Dialog from "@radix-ui/react-dialog";
import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cn } from "../lib/utils";

const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 rounded-full font-medium transition active:scale-[0.98] disabled:pointer-events-none disabled:opacity-40",
  {
    variants: {
      variant: {
        brass: "bg-[#e2b15a] text-[#1a140c] hover:bg-[#efc57a]",
        ghost: "bg-white/5 text-[#f4efe6] hover:bg-white/10",
        quiet: "bg-transparent text-[#b7ab9c] hover:text-[#f4efe6]",
        danger: "bg-[#6e3030] text-[#f4efe6] hover:bg-[#824040]",
      },
      size: {
        md: "h-11 px-4 text-sm",
        lg: "h-14 px-6 text-base",
      },
    },
    defaultVariants: { variant: "brass", size: "md" },
  },
);

export function Button({
  className,
  variant,
  size,
  asChild = false,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Comp = asChild ? Slot : "button";
  return <Comp className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field grid gap-1 text-sm text-[#b7ab9c]">
      {label}
      {children}
    </label>
  );
}

export const inputClass =
  "h-11 rounded-xl border border-white/10 bg-black/30 px-3 text-[#f4efe6] outline-none focus:border-[#e2b15a]";

export function Modal({
  open,
  title,
  onClose,
  children,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/70" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[88vh] w-[min(640px,calc(100%-24px))] -translate-x-1/2 -translate-y-1/2 overflow-auto rounded-3xl border border-white/10 bg-[#1b1713] p-5 text-[#f4efe6] shadow-2xl">
          <Dialog.Title className="font-serif text-2xl">{title}</Dialog.Title>
          <div className="mt-4">{children}</div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
