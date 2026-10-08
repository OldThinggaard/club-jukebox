import { Admin } from "./Admin";
import { Hall } from "./Hall";
import { Kiosk } from "./Kiosk";

export default function App() {
  const path = window.location.pathname;
  if (path.startsWith("/player")) return <Hall />;
  if (path.startsWith("/admin")) return <Admin />;
  return <Kiosk />;
}
